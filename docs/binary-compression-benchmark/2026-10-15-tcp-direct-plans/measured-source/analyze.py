"""Summarize paired, actual-socket compression benchmarks; Python standard library only.

Usage: python analyze.py input.json output-prefix
Writes output-prefix.json and output-prefix.md. Input measurements remain untouched.
"""

import argparse
import json
import math
import random
import statistics
from collections import defaultdict
from pathlib import Path


PHASE_ORDER = {"cold": 0, "warm": 1, "latency": 2}
BOOTSTRAP_SAMPLES = 10_000
SEED = 7301


def median(values):
    return statistics.median(values)


def throughput(row):
    return row["payloadBytes"] / 1048576 / (row["elapsedMs"] / 1000)


def socket_bytes(row):
    return row["upBytes"] + row["downBytes"]


def geometric_mean(values):
    return math.exp(statistics.mean(math.log(value) for value in values))


def paired_ratio(off, on, metric, lower_is_better=False):
    """Bootstrap matched rounds; ratios >1 always mean enabled is better."""
    pairs = sorted(set(off) & set(on))
    values = []
    for round_id in pairs:
        baseline, enabled = metric(off[round_id]), metric(on[round_id])
        if baseline <= 0 or enabled <= 0:
            continue
        values.append(baseline / enabled if lower_is_better else enabled / baseline)
    if not values:
        return {"pairs": 0, "ratio": None, "ci95": None}
    estimate = geometric_mean(values)
    interval = None
    if len(values) >= 2:
        rng = random.Random(SEED)
        logs = [math.log(value) for value in values]
        draws = sorted(
            math.exp(statistics.mean(rng.choices(logs, k=len(logs))))
            for _ in range(BOOTSTRAP_SAMPLES)
        )
        interval = [draws[int(.025 * BOOTSTRAP_SAMPLES)], draws[int(.975 * BOOTSTRAP_SAMPLES) - 1]]
    return {"pairs": len(values), "ratio": estimate, "ci95": interval}


def summary(rows):
    result = {
        "runs": len(rows),
        "medianUsefulMiBPerSecond": median([throughput(row) for row in rows]),
        "medianElapsedMs": median([row["elapsedMs"] for row in rows]),
        "medianPayloadBytes": median([row["payloadBytes"] for row in rows]),
        "medianMessages": median([row["messages"] for row in rows]),
        "medianRttP50Ms": median([row["rttP50Ms"] for row in rows]),
        "medianRttP95Ms": median([row["rttP95Ms"] for row in rows]),
        "medianRttP99Ms": median([row["rttP99Ms"] for row in rows]),
        "medianUpBytes": median([row["upBytes"] for row in rows]),
        "medianDownBytes": median([row["downBytes"] for row in rows]),
        "medianTotalBytes": median([socket_bytes(row) for row in rows]),
        "medianCpuMs": median([row["cpuMs"] for row in rows]),
        "medianDictionaryCount": median([row["dictionaryCount"] for row in rows]),
        "medianCacheBytes": median([row["cacheBytes"] for row in rows]),
        "medianSettleMs": median([row["settleMs"] for row in rows]),
    }
    if all("allocatedBytes" in row for row in rows):
        result["medianAllocatedBytes"] = median([row["allocatedBytes"] for row in rows])
    for key in ("candidateCount", "encodingAttempts", "bypassedMessages", "probeInterval", "planCount", "planHits", "planHitsDuringPhase", "learningPasses"):
        if all(key in row for row in rows):
            result["median" + key[0].upper() + key[1:]] = median([row[key] for row in rows])
    return result


def percent_reduction(off, on, metric):
    pairs = sorted(set(off) & set(on))
    values = [100 * (1 - metric(on[r]) / metric(off[r])) for r in pairs if metric(off[r]) > 0]
    return median(values) if values else None


def compare(groups):
    off, on = groups.get(False, {}), groups.get(True, {})
    pair_ids = sorted(set(off) & set(on))
    for round_id in pair_ids:
        for key in ("payloadBytes", "messages"):
            if off[round_id][key] != on[round_id][key]:
                raise ValueError(f"Unequal {key} in paired round {round_id}")
    result = {
        "off": summary(list(off.values())) if off else None,
        "on": summary(list(on.values())) if on else None,
        "pairedRounds": pair_ids,
        "throughputRatioOnOverOff": paired_ratio(off, on, throughput),
        "p50LatencySpeedupOffOverOn": paired_ratio(off, on, lambda r: r["rttP50Ms"], True),
        "p95LatencySpeedupOffOverOn": paired_ratio(off, on, lambda r: r["rttP95Ms"], True),
        "p99LatencySpeedupOffOverOn": paired_ratio(off, on, lambda r: r["rttP99Ms"], True),
        "medianPairedTotalByteReductionPercent": percent_reduction(off, on, socket_bytes),
        "medianPairedCpuReductionPercent": percent_reduction(off, on, lambda r: r["cpuMs"]),
    }
    if off and on and all("allocatedBytes" in row for row in [*off.values(), *on.values()]):
        result["medianPairedAllocationReductionPercent"] = percent_reduction(off, on, lambda row: row["allocatedBytes"])
    return result


def validate(row, phase_order=PHASE_ORDER):
    required = (
        "protocol", "workload", "phase", "enabled", "round", "elapsedMs", "payloadBytes",
        "messages", "rttP50Ms", "rttP95Ms", "rttP99Ms", "upBytes", "downBytes", "setupMs",
        "setupUpBytes", "setupDownBytes", "cpuMs", "dictionaryCount", "cacheBytes", "settleMs",
        "priorUpBytes", "priorDownBytes",
    )
    missing = set(required) - row.keys()
    if missing:
        raise ValueError(f"Missing fields: {sorted(missing)}")
    if row["phase"] not in phase_order:
        raise ValueError(f"Unknown phase: {row['phase']}")
    if type(row["enabled"]) is not bool:
        raise ValueError("enabled must be a JSON boolean")
    for key in required[4:]:
        value = row[key]
        if not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
            raise ValueError(f"Invalid nonnegative numeric field {key}: {value}")
    if row["elapsedMs"] <= 0:
        raise ValueError("elapsedMs must be positive")
    for key in ("allocatedBytes", "candidateCount", "encodingAttempts", "bypassedMessages", "probeInterval", "planCount", "planHits"):
        if key in row and (type(row[key]) not in (int, float) or not math.isfinite(row[key]) or row[key] < 0):
            raise ValueError(f"Invalid nonnegative optional numeric field {key}: {row[key]}")


def add_plan_hit_deltas(measurements, phase_orders):
    """Derive per-phase hits from cumulative counters, only across adjacent phases."""
    previous = {}
    def order(row):
        key = (row["protocol"], row["workload"], row["enabled"], row["round"])
        phase_index = phase_orders.get(row["workload"], PHASE_ORDER)[row["phase"]]
        return key, phase_index
    for row in sorted(measurements, key=order):
        if "planHits" not in row:
            continue
        key, phase_index = order(row)
        earlier = previous.get(key)
        if phase_index == 0:
            row["planHitsDuringPhase"] = row["planHits"]
        elif earlier and earlier[0] + 1 == phase_index:
            delta = row["planHits"] - earlier[1]
            if delta < 0:
                raise ValueError(f"Decreasing plan-hit counter in {key}/{row['phase']}")
            row["planHitsDuringPhase"] = delta
        previous[key] = phase_index, row["planHits"]


def build_overall(measurements, phase_orders=None):
    runs = defaultdict(dict)
    for row in measurements:
        key = (row["protocol"], row["workload"], row["enabled"], row["round"])
        runs[key][row["phase"]] = row
    grouped = defaultdict(lambda: defaultdict(dict))
    incomplete = []
    for (protocol, workload, enabled, round_id), phases in sorted(runs.items()):
        phase_order = phase_orders.get(workload, PHASE_ORDER) if phase_orders else PHASE_ORDER
        if set(phases) != set(phase_order):
            incomplete.append({"protocol": protocol, "workload": workload, "enabled": enabled,
                               "round": round_id, "missingPhases": sorted(set(phase_order) - phases.keys())})
            continue
        ordered = [phases[phase] for phase in phase_order]
        first = ordered[0]
        for row in ordered[1:]:
            for key in ("setupMs", "setupUpBytes", "setupDownBytes"):
                if row[key] != first[key]:
                    raise ValueError(f"Setup fields differ across phases in {protocol}/{workload}/{enabled}/{round_id}")
        # Each prior counter is cumulative from the setup snapshot to the start
        # of that phase. Use the final endpoint rather than summing intervals:
        # a heartbeat/control packet in a gap between phase snapshots still costs
        # network bytes, even though it belongs to no timed phase.
        end_up = end_down = 0
        between_up = between_down = 0
        for row in ordered:
            if row["priorUpBytes"] < end_up or row["priorDownBytes"] < end_down:
                raise ValueError(f"Overlapping or decreasing cumulative byte counters in {protocol}/{workload}/{enabled}/{round_id}/{row['phase']}")
            between_up += row["priorUpBytes"] - end_up
            between_down += row["priorDownBytes"] - end_down
            end_up = row["priorUpBytes"] + row["upBytes"]
            end_down = row["priorDownBytes"] + row["downBytes"]
        value = {
            "upBytes": first["setupUpBytes"] + end_up,
            "downBytes": first["setupDownBytes"] + end_down,
            "setupBytes": first["setupUpBytes"] + first["setupDownBytes"],
            "setupMs": first["setupMs"],
            "betweenPhaseUpBytes": between_up,
            "betweenPhaseDownBytes": between_down,
            "betweenPhaseBytes": between_up + between_down,
            "payloadBytes": sum(row["payloadBytes"] for row in ordered),
            "messages": sum(row["messages"] for row in ordered),
        }
        grouped[(protocol, workload)][enabled][round_id] = value
    results = []
    for (protocol, workload), modes in sorted(grouped.items()):
        off, on = modes.get(False, {}), modes.get(True, {})
        result = {"protocol": protocol, "workload": workload}
        for mode, rows in (("off", off), ("on", on)):
            result[mode] = None if not rows else {
                "runs": len(rows),
                "medianUpBytes": median([r["upBytes"] for r in rows.values()]),
                "medianDownBytes": median([r["downBytes"] for r in rows.values()]),
                "medianTotalBytes": median([socket_bytes(r) for r in rows.values()]),
                "medianSetupBytes": median([r["setupBytes"] for r in rows.values()]),
                "medianSetupMs": median([r["setupMs"] for r in rows.values()]),
                "medianBetweenPhaseUpBytes": median([r["betweenPhaseUpBytes"] for r in rows.values()]),
                "medianBetweenPhaseDownBytes": median([r["betweenPhaseDownBytes"] for r in rows.values()]),
                "medianBetweenPhaseBytes": median([r["betweenPhaseBytes"] for r in rows.values()]),
                "medianPayloadBytes": median([r["payloadBytes"] for r in rows.values()]),
                "medianMessages": median([r["messages"] for r in rows.values()]),
            }
        result["pairedRounds"] = sorted(set(off) & set(on))
        result["medianPairedTotalByteReductionPercent"] = percent_reduction(off, on, socket_bytes)
        results.append(result)
    return results, incomplete


def number(value, decimals=2):
    return "—" if value is None else f"{value:,.{decimals}f}"


def ratio_text(result):
    if result["ratio"] is None:
        return "—"
    text = f"{result['ratio']:.3f}×"
    if result["ci95"]:
        low, high = result["ci95"]
        text += f" [{low:.3f}, {high:.3f}]"
    return text


def markdown(result):
    lines = [
        "# Binary compression transfer benchmark",
        "",
        f"Source: `{result['source']}`. Per-mode figures are medians of complete runs. "
        "On/off comparisons pair the same round. Compression is the only setting changed.",
        "",
        "Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, "
        "application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. "
        "Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending "
        "cumulative counter, including traffic between phase snapshots.",
        "",
        "Bulk throughput is useful application payload divided by the time from the first send through the last "
        "validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses "
        "one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded "
        "from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results "
        "without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.",
        "",
        "Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples "
        "(seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. "
        "Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the "
        "maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm "
        "phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process "
        "and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. "
        "Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.",
        "",
        "| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |",
        "|---|---|---|---:|---:|---:|---|---:|---:|---:|",
    ]
    for row in result["comparisons"]:
        off, on = row["off"], row["on"]
        lines.append("| " + " | ".join([
            row["protocol"], row["workload"], row["phase"], str(len(row["pairedRounds"])),
            number(off["medianUsefulMiBPerSecond"] if off else None),
            number(on["medianUsefulMiBPerSecond"] if on else None),
            ratio_text(row["throughputRatioOnOverOff"]),
            number(off["medianTotalBytes"] if off else None, 0),
            number(on["medianTotalBytes"] if on else None, 0),
            number(row["medianPairedTotalByteReductionPercent"]) + "%",
        ]) + " |")
    if any(row["on"] and "medianAllocatedBytes" in row["on"] for row in result["comparisons"]):
        lines.extend([
            "", "## Adaptive state and managed allocation", "",
            "Adaptive counters and cache state are cumulative through the end of the phase, after its drain. "
            "Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). "
            "Allocation covers only the timed phase in the client process, including the proxy; it is approximate "
            "allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control "
            "messages, which can include queue-drain application barriers as well as measured payloads.", "",
            "| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |",
            "|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|",
        ])
        for row in result["comparisons"]:
            off, on = row["off"], row["on"]
            lines.append("| " + " | ".join([
                row["protocol"], row["workload"], row["phase"],
                f"{number(off.get('medianAllocatedBytes') if off else None, 0)} / {number(on.get('medianAllocatedBytes') if on else None, 0)}",
                *[number(on.get(key) if on else None, 0) for key in ("medianCandidateCount", "medianEncodingAttempts", "medianBypassedMessages", "medianProbeInterval", "medianPlanCount", "medianPlanHits", "medianPlanHitsDuringPhase")],
            ]) + " |")
    lines.extend([
        "", "## Application round-trip latency", "",
        "| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |",
        "|---|---|---|---|---|---|---|---|---:|---:|",
    ])
    for row in result["comparisons"]:
        off, on = row["off"], row["on"]
        def pair(key):
            return f"{number(off[key] if off else None, 3)} / {number(on[key] if on else None, 3)}"
        lines.append("| " + " | ".join([
            row["protocol"], row["workload"], row["phase"], pair("medianRttP50Ms"),
            pair("medianRttP95Ms"), pair("medianRttP99Ms"),
            ratio_text(row["p95LatencySpeedupOffOverOn"]), pair("medianCpuMs"),
            number(on["medianDictionaryCount"] if on else None, 0),
            number(on["medianCacheBytes"] if on else None, 0),
        ]) + " |")
    lines.extend([
        "", "## Complete connection byte cost", "",
        "Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. "
        "Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.", "",
        "| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |",
        "|---|---|---:|---:|---:|---:|---:|---|---|---|",
    ])
    for row in result["overall"]:
        off, on = row["off"], row["on"]
        lines.append("| " + " | ".join([
            row["protocol"], row["workload"], str(len(row["pairedRounds"])),
            number(off["medianPayloadBytes"] if off else on["medianPayloadBytes"], 0),
            number(off["medianTotalBytes"] if off else None, 0),
            number(on["medianTotalBytes"] if on else None, 0),
            number(row["medianPairedTotalByteReductionPercent"]) + "%",
            f"{number(off['medianBetweenPhaseBytes'] if off else None, 0)} / {number(on['medianBetweenPhaseBytes'] if on else None, 0)}",
            f"{number(off['medianSetupBytes'] if off else None, 0)} / {number(on['medianSetupBytes'] if on else None, 0)}",
            f"{number(off['medianSetupMs'] if off else None)} / {number(on['medianSetupMs'] if on else None)}",
        ]) + " |")
    if result["incompleteRuns"]:
        lines.extend(["", f"{len(result['incompleteRuns'])} incomplete runs were excluded from lifetime totals; phase rows remain visible."])
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("output_prefix", type=Path)
    args = parser.parse_args()
    document = json.loads(args.input.read_text(encoding="utf-8-sig"))
    measurements = document["measurements"]
    if not measurements:
        raise ValueError("No measurements supplied")
    phase_orders = {workload: {phase["name"]: index for index, phase in enumerate(plan)}
                    for workload, plan in document.get("phasePlans", {}).items()}
    groups = defaultdict(lambda: defaultdict(dict))
    for row in measurements:
        validate(row, phase_orders.get(row["workload"], PHASE_ORDER))
        group_key = (row["protocol"], row["workload"], row["phase"])
        rounds = groups[group_key][row["enabled"]]
        if row["round"] in rounds:
            raise ValueError(f"Duplicate measurement: {group_key}, {row['enabled']}, {row['round']}")
        rounds[row["round"]] = row
    add_plan_hit_deltas(measurements, phase_orders)
    comparisons = []
    for (protocol, workload, phase), modes in sorted(groups.items(), key=lambda item: (item[0][0], item[0][1], phase_orders.get(item[0][1], PHASE_ORDER)[item[0][2]])):
        comparisons.append({"protocol": protocol, "workload": workload, "phase": phase, **compare(modes)})
    overall, incomplete = build_overall(measurements, phase_orders)
    result = {
        "source": str(args.input.resolve()),
        "metadata": {key: value for key, value in document.items() if key != "measurements"},
        "method": {
            "summary": "Median of per-run metrics; paired geometric mean ratios with percentile bootstrap confidence intervals.",
            "bootstrapSamples": BOOTSTRAP_SAMPLES,
            "seed": SEED,
            "confidence": .95,
            "bytes": "Both directions once, including all recorded dictionary/control/application acknowledgement traffic and framing; network-layer headers excluded.",
            "overallBytes": "Ending cumulative counter: setup bytes plus final phase prior and phase bytes, including all interphase gaps; only complete connections included.",
        },
        "comparisons": comparisons,
        "overall": overall,
        "incompleteRuns": incomplete,
    }
    json_path = Path(str(args.output_prefix) + ".json")
    markdown_path = Path(str(args.output_prefix) + ".md")
    if args.input.resolve() in (json_path.resolve(), markdown_path.resolve()):
        raise ValueError("Output prefix would overwrite input")
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    markdown_path.write_text(markdown(result), encoding="utf-8")
    print(json.dumps({"analysisJson": str(json_path.resolve()), "reportMarkdown": str(markdown_path.resolve()),
                      "measurements": len(measurements), "comparisons": len(comparisons), "overall": len(overall)}))


if __name__ == "__main__":
    main()
