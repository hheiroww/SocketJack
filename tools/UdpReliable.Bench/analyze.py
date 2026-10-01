"""Paired bootstrap comparison of complete alternating benchmark rounds; standard library only."""
import json
import random
import statistics
import sys
from pathlib import Path

source = Path(sys.argv[1])
document = json.loads(source.read_text(encoding="utf-8"))
groups = {}
for measurement in document["Measurements"]:
    groups.setdefault(measurement["Mode"], {})[measurement["Run"]] = measurement

def interval(values):
    rng = random.Random(7301)
    draws = sorted(statistics.mean(rng.choices(values, k=len(values))) for _ in range(20000))
    return [draws[499], draws[19499]]

summary = []
for mode, runs in groups.items():
    summary.append({"Mode": mode, "Runs": len(runs),
                    "MeanMiBPerSecond": statistics.mean(r["MiBPerSecond"] for r in runs.values()),
                    "MeanP95Milliseconds": statistics.mean(r["P95Milliseconds"] for r in runs.values()),
                    "Retransmissions": None if any(r["Retransmissions"] is None for r in runs.values()) else sum(r["Retransmissions"] for r in runs.values())})
comparisons = []
for mode in (m for m in groups if m not in ("tcp", "raw-tcp")):
    for baseline in ("tcp", "raw-tcp"):
        pairs = sorted(set(groups[mode]) & set(groups[baseline]))
        speed = [groups[mode][r]["MiBPerSecond"] / groups[baseline][r]["MiBPerSecond"] - 1 for r in pairs]
        latency = [1 - groups[mode][r]["P95Milliseconds"] / groups[baseline][r]["P95Milliseconds"] for r in pairs]
        throughput_ci, latency_ci = interval(speed), interval(latency)
        comparisons.append({"Mode": mode, "Baseline": baseline, "Pairs": len(pairs),
                            "ThroughputImprovement": statistics.mean(speed), "Throughput95CI": throughput_ci,
                            "P95LatencyReduction": statistics.mean(latency), "Latency95CI": latency_ci,
                            "Pass": len(pairs) >= 10 and throughput_ci[0] >= .10 and latency_ci[0] >= .10})
result = {"Source": source.name, "Method": "Mean paired relative improvements; 20,000 paired bootstrap samples, seed 7301; percentile 95% intervals.",
          "Summary": summary, "Comparisons": comparisons,
          "Acceptance": "PASS" if any(all(c["Pass"] for c in comparisons if c["Mode"] == mode) for mode in (m for m in groups if m not in ("tcp", "raw-tcp"))) else "FAIL"}
target = source.with_name(source.stem + "-analysis.json")
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result, indent=2))
