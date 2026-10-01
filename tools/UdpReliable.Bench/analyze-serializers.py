"""Summarize the alternating same-payload codec comparison; standard library only."""
import json
import random
import statistics
import sys
from pathlib import Path

source = Path(sys.argv[1])
document = json.loads(source.read_text(encoding="utf-8"))
groups = {}
for row in document["Measurements"]:
    groups.setdefault((row["PayloadBytes"], row["Mode"]), {})[row["Run"]] = row
summary, comparisons = [], []
for (size, mode), rows in sorted(groups.items()):
    summary.append({"PayloadBytes": size, "Mode": mode, "Runs": len(rows),
        **{key: statistics.mean(r[key] for r in rows.values()) for key in (
            "EncodedBytes", "EncodeNanoseconds", "DecodeNanoseconds", "DecodeAndUnwrapNanoseconds",
            "EncodeAllocatedBytes", "DecodeOnlyAllocatedBytes", "DecodeAllocatedBytes")}})
for size in sorted({key[0] for key in groups}):
    for baseline in ("binary-before", "json"):
        current, previous = groups[size, "binary"], groups[size, baseline]
        pairs = sorted(set(current) & set(previous))
        for metric in ("EncodeNanoseconds", "DecodeNanoseconds", "DecodeAndUnwrapNanoseconds"):
            a = [previous[i][metric] for i in pairs]
            b = [current[i][metric] for i in pairs]
            rng = random.Random(7301)
            draws = []
            for _ in range(20000):
                indexes = rng.choices(range(len(pairs)), k=len(pairs))
                draws.append(sum(a[i] for i in indexes) / sum(b[i] for i in indexes))
            draws.sort()
            comparisons.append({"PayloadBytes": size, "Baseline": baseline, "Metric": metric,
                "Speedup": statistics.mean(a) / statistics.mean(b), "Speedup95CI": [draws[499], draws[19499]]})
result = {"Source": source.name, "Method": "Ratio of mean operation times; paired bootstrap 20,000 draws, seed 7301, percentile 95% CI. DecodeAndUnwrap includes current type/assembly checks.",
    "Summary": summary, "Comparisons": comparisons}
target = source.with_name(source.stem + "-analysis.json")
target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result, indent=2))
