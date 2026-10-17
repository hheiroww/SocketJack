"""Compare two complete analyze.py outputs without claiming cross-session pairing.

Usage: python compare.py previous-analysis.json optimized-analysis.json output-prefix
The paired on/off ratios remain within each experiment. Absolute old/new medians
are sequential measurements and can also reflect machine or baseline drift.
"""

import argparse
import json
from pathlib import Path


def number(value, decimals=2):
    return "—" if value is None else f"{value:,.{decimals}f}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("previous", type=Path)
    parser.add_argument("optimized", type=Path)
    parser.add_argument("output_prefix", type=Path)
    args = parser.parse_args()
    previous = json.loads(args.previous.read_text(encoding="utf-8-sig"))
    optimized = json.loads(args.optimized.read_text(encoding="utf-8-sig"))
    old = {(row["protocol"], row["workload"], row["phase"]): row for row in previous["comparisons"]}
    new = {(row["protocol"], row["workload"], row["phase"]): row for row in optimized["comparisons"]}
    old_total = {(row["protocol"], row["workload"]): row for row in previous["overall"]}
    new_total = {(row["protocol"], row["workload"]): row for row in optimized["overall"]}
    rows = []
    for key in sorted(old.keys() & new.keys()):
        before, after = old[key], new[key]
        if not all(row[mode] for row in (before, after) for mode in ("off", "on")):
            continue
        for mode in ("off", "on"):
            if before[mode]["medianPayloadBytes"] != after[mode]["medianPayloadBytes"] or before[mode]["medianMessages"] != after[mode]["medianMessages"]:
                raise ValueError(f"Different useful workload in {key}/{mode}")
        rows.append({
            "protocol": key[0], "workload": key[1], "phase": key[2],
            "previous": before, "optimized": after,
            "enabledMedianThroughputRatioOptimizedOverPrevious": after["on"]["medianUsefulMiBPerSecond"] / before["on"]["medianUsefulMiBPerSecond"],
            "disabledMedianThroughputRatioOptimizedOverPrevious": after["off"]["medianUsefulMiBPerSecond"] / before["off"]["medianUsefulMiBPerSecond"],
        })
    result = {
        "previousSource": str(args.previous.resolve()), "optimizedSource": str(args.optimized.resolve()),
        "scope": "Absolute previous/optimized medians are sequential experiments, not paired samples. Paired on/off estimates and intervals are retained separately within each experiment. Useful workloads must match.",
        "previousCoreSha256": previous["metadata"]["coreSha256"],
        "optimizedCoreSha256": optimized["metadata"]["coreSha256"],
        "comparisons": rows,
    }
    lines = [
        "# Previous versus optimized binary compression", "", result["scope"], "",
        "Both experiments use the same deterministic fixtures, process isolation, phase counts, window sizes, "
        "transport settings, and socket-byte accounting. The optimized harness additionally records managed allocations "
        "and adaptive counters outside timing. Absolute throughput can vary between the two sessions; the new on/off "
        "comparison is the primary evidence for current overhead.", "",
        "## Warm throughput", "",
        "| Transport | Workload | Previous off/on MiB/s | Optimized off/on MiB/s | Previous paired on/off [95% CI] | Optimized paired on/off [95% CI] |",
        "|---|---|---:|---:|---|---|",
    ]
    def paired(row):
        value = row["throughputRatioOnOverOff"]
        low, high = value["ci95"]
        return f"{value['ratio']:.3f}× [{low:.3f}, {high:.3f}]"
    for row in rows:
        if row["phase"] != "warm":
            continue
        before, after = row["previous"], row["optimized"]
        pair = lambda item: f"{number(item['off']['medianUsefulMiBPerSecond'])} / {number(item['on']['medianUsefulMiBPerSecond'])}"
        lines.append(f"| {row['protocol']} | {row['workload']} | {pair(before)} | {pair(after)} | {paired(before)} | {paired(after)} |")
    lines.extend(["", "## Complete connection bytes", "",
                  "Includes setup, training, updates, acknowledgements, all phases and interphase traffic in both directions.", "",
                  "| Transport | Workload | Previous off/on bytes | Optimized off/on bytes | Previous saved | Optimized saved |",
                  "|---|---|---:|---:|---:|---:|"])
    for key in sorted(old_total.keys() & new_total.keys()):
        before, after = old_total[key], new_total[key]
        pair = lambda item: f"{number(item['off']['medianTotalBytes'], 0)} / {number(item['on']['medianTotalBytes'], 0)}"
        lines.append(f"| {key[0]} | {key[1]} | {pair(before)} | {pair(after)} | {number(before['medianPairedTotalByteReductionPercent'])}% | {number(after['medianPairedTotalByteReductionPercent'])}% |")
    json_path = Path(str(args.output_prefix) + ".json")
    markdown_path = Path(str(args.output_prefix) + ".md")
    if {args.previous.resolve(), args.optimized.resolve()} & {json_path.resolve(), markdown_path.resolve()}:
        raise ValueError("Output would overwrite an input")
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    markdown_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps({"comparisonJson": str(json_path.resolve()), "comparisonMarkdown": str(markdown_path.resolve()), "comparisons": len(rows)}))


if __name__ == "__main__":
    main()
