"""Render this run's evidence without mixing earlier DLLs into the statistics."""
import json
from pathlib import Path
from statistics import median

HERE = Path(__file__).resolve().parent
def read(name):
    return json.loads((HERE / name).read_text(encoding="utf-8-sig"))

main, sustained, adaptive, fanout = [read(name) for name in (
    "analysis.json", "sustained-analysis.json", "adaptive-analysis.json", "fanout.json")]
verification = read("verification.json")
previous = json.loads((HERE.parent / "2026-10-15-tcp-optimized/analysis.json").read_text(encoding="utf-8-sig"))

def comparison(data, workload, phase):
    return next(row for row in data["comparisons"] if row["workload"] == workload and row["phase"] == phase)

def ratio(value):
    lo, hi = value["ci95"]
    return f'{value["ratio"]:.3f}× [{lo:.3f}, {hi:.3f}]'

def number(value):
    return f"{value:,.0f}"

def rows_table(data, phase):
    lines = ["| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |",
             "|---|---:|---|---:|"]
    for row in data["comparisons"]:
        if row["phase"] != phase:
            continue
        off, on = row["off"], row["on"]
        lines.append(f'| `{row["workload"]}` | {off["medianUsefulMiBPerSecond"]:.2f} → {on["medianUsefulMiBPerSecond"]:.2f} | '
                     f'{ratio(row["throughputRatioOnOverOff"])} | {off["medianRttP95Ms"]:.3f} → {on["medianRttP95Ms"]:.3f} |')
    return "\n".join(lines)

def bytes_table(data):
    lines = ["| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |",
             "|---|---:|---:|---:|"]
    for row in data["overall"]:
        lines.append(f'| `{row["workload"]}` | {number(row["off"]["medianTotalBytes"])} | '
                     f'{number(row["on"]["medianTotalBytes"])} | {row["medianPairedTotalByteReductionPercent"]:.3f}% |')
    return "\n".join(lines)

on_fan = [row for row in fanout["measurements"] if row["enabled"]]
off_fan = [row for row in fanout["measurements"] if not row["enabled"]]
fan_on, fan_off = median(row["totalBytes"] for row in on_fan), median(row["totalBytes"] for row in off_fan)
classifications = []
for row in main["comparisons"]:
    if row["phase"] != "warm":
        continue
    value = row["throughputRatioOnOverOff"]
    state = "faster" if value["ci95"][0] > 1 else "slower" if value["ci95"][1] < 1 else "no established directional difference"
    classifications.append(f'- `{row["workload"]}`: {state}; {ratio(value)}.')

text = f'''# TCP compression optimization and static HTTP caching

This report measures the final Release DLL with `BinarySerializer.EnableCompression` on and off, including cold connections, partially changing objects, and longer transfers. Byte savings and transfer speed are reported separately. All controls and dictionary updates are counted. Timing does not imply that compression is free or that every workload becomes faster.

Core SHA-256: **`{verification["coreSha256"]}`**. The main series has ten paired rounds; the sustained series has six; adaptive and shared-fanout series have five each. All {verification["byteVerifiedObjects"]:,} delivered objects were byte-checked. Separate-process endpoints generate their fixtures independently; the fanout experiment shares one process and one expected fixture. All final series use this same DLL. The previous intermediate run is retained separately and is never pooled into these statistics.

## Implementation

- Cold, unproven random traffic uses bounded sampled hashes. Periodic rotating grids check between the fixed samples; exact candidate learning starts only after evidence of recurrence. The fresh-random test sends 256 objects with zero encoding attempts, zero exact learning passes and no retained payload candidates.
- Profitable, verified encoding plans skip the local repetition scan. Every referenced span still receives a byte comparison; changed bytes remain literals. Plans remain bounded and are invalidated when the dictionary changes. Periodic exact learning continues on useful streams so newly stable fields can join the dictionary.
- Stream dictionary updates use bounded 16 KiB batches, reducing the number of update frames and acknowledgements during training. Ordinary UDP retains its datagram bound. A peer supplying an immutable definition is already known to own it and can receive a referenced reply immediately.
- Sample hashing, dictionary-ID reads, and cache lookup are cheaper. Chunk boundaries favor larger definitions within the unchanged 64–1024-byte wire limits. Dictionary-only frames can deserialize directly from literal and immutable dictionary slices, avoiding a temporary expanded-message buffer. Local LZ, inline definitions and larger segment layouts retain the full checked decoder fallback.
- SB1 and SBC v1 wire formats, negotiation, whitelist/authorization checks, private/shared scope, browser consent and the process-wide retained-state ceiling of physical RAM / 16 remain in effect. Each native repository is additionally capped at 4 MiB. Compression is default-on only for the custom `BinarySerializer` and can be disabled before connecting.

## Short-connection warm throughput

Each connection sends 64 cold objects, 128 warm objects and 64 stop-and-wait latency objects. Bulk throughput includes validated receipt delivery with eight outstanding messages. In this table, p95 is the **loaded bulk RTT**, not the separate stop-and-wait result.

{rows_table(main, "warm")}

Paired geometric means and 10,000 paired bootstrap resamples produce the 95% intervals. An interval crossing 1 does not establish a speed difference. The medians in the speed columns are computed independently and therefore their quotient is not the paired geometric mean. Current warm classifications are:

{chr(10).join(classifications)}

## Cold starts

Cold timings include first-use JIT, formatting, cache setup, dictionary training and actual receipt delivery. The first-object case sends no throwaway payload. Its following 63-object cold phase is separated by the normal quiet drain; it therefore differs from the uninterrupted 64-object delta cold phase. It uses exactly the same 75%-stable payload pattern.

{rows_table(main, "first")}

{rows_table(main, "cold")}

Cold costs are material and remain visible here and in complete connection totals. Applications with isolated short-lived requests can disable binary compression.

## Sustained throughput and latency

These trials use the same deterministic payload patterns but send 64 cold, **2,048 warm**, and **256 stop-and-wait latency** objects per fresh connection. They give the process and cache more time to settle. They are reported separately, rather than replacing short-connection or first-use results. Warm p95 below still uses a bulk window of eight.

{rows_table(sustained, "warm")}

Stop-and-wait p95 has one outstanding object:

{rows_table(sustained, "latency")}

Short-connection stop-and-wait results:

{rows_table(main, "latency")}

These are medians of per-trial tail latencies, not pooled message percentiles. With 64 samples, short-case p99 is effectively a maximum; complete p50/p95/p99 and confidence intervals are retained in the linked analyses.

## Total data, including updates

Totals count both directions exactly once, including setup, capability negotiation, dictionary definitions, acknowledgements, framing, application receipts, trailing controls and gaps between phase snapshots. They exclude OS TCP acknowledgements, IP/TCP/link headers, and the harness shutdown message. Retained binary definitions are native memory state; HTTP browser caching is a separate feature.

Short connections:

{bytes_table(main)}

Sustained connections:

{bytes_table(sustained)}

Fully random data does not shrink; the extra negotiation bytes remain in the total. Smaller byte totals alone do not establish a throughput or latency improvement on unconstrained loopback.

## Adaptive traffic and shared propagation

The adaptive stream changes from random data to repetition, random churn, an old pattern returning after 64 unrelated objects, and local runs. The spaced case cycles 32 different objects. The marginal case repeats only 512 bytes (1.6%) of otherwise changing random data. Private connection caches persist across phases. See [all adaptive phase timings and state](adaptive-analysis.md).

{bytes_table(adaptive)}

The separate shared-repository experiment uses one active uploader and three idle clients. It counts all four links, including the idle clients' dictionary traffic. Median complete bytes are **{number(fan_off)} off → {number(fan_on)} on**, a **{100 * (1-fan_on/fan_off):.2f}%** reduction. Idle-client data-phase traffic with compression has median **{number(median(row["dataIdleTotalBytes"] for row in on_fan))} bytes**; their definition counts are recorded in [raw fanout data](fanout.json). Timing is auxiliary because the endpoints share one process. Independent website users continue to negotiate private repositories and do not receive this fanout.

## Earlier iteration

The [intermediate measurements](../2026-10-15-tcp-optimized/analysis.md) used DLL `C422ECED0F297DB562E4156F2147923117426FB85F5B9E44CDD188B2F0D433F9`. That iteration prompted the final cold-scout, plan and stream-batching changes. The descriptive paired-ratio comparison below does not pair trials across different versions or hold desktop background load constant. Absolute cross-run rates should not be attributed entirely to code changes.

| Warm workload | Intermediate on/off speed [95% CI] | Final on/off speed [95% CI] |
|---|---|---|
'''
for name in ("delta-32k", "delta-blocks-32k", "random-32k", "repeated-32k"):
    text += f'| `{name}` | {ratio(comparison(previous, name, "warm")["throughputRatioOnOverOff"])} | {ratio(comparison(main, name, "warm")["throughputRatioOnOverOff"])} |\n'

text += f'''
## HTTP caching

`HttpServer` and `MutableTcpServer.Http` now apply conditional caching only to physical `MapFile` and `MapDirectory` responses. GET/HEAD assets get a SHA-256 content ETag, `Last-Modified`, and `public, max-age=0, must-revalidate` (or private for authenticated/personal requests). A matching validator returns 304. Changed file bytes reset the ETag even with unchanged size and timestamps; deletion returns an uncached error.

Dynamic handlers, generated responses, templates and streaming routes use `no-store`. A `.js` URL does not make generated output static. Rendered `RouteFile`/`RouteString` content is recomputed on every request. Explicit `no-store` disables static caching too. Static validation reads the current file without a second retained RAM copy; this costs file I/O but avoids stale metadata-only caches. See [policy and scope](../../HTTP-caching.md). Seven new HTTP cases include real listener requests and hosted dispatch. This verifies library behavior; it is not a new production Portal deployment.

## Method and verification

Each main/sustained/adaptive trial starts separate fresh server and client processes on IPv4 loopback with a transparent counting proxy in the client process. On/off order alternates and workload order rotates. Both modes use identical settings except `BinarySerializer.EnableCompression`. SafeMode and typed admission are enabled; caches are private on both ends. Outer compression, legacy pattern caching, P2P, chunking, FPS and bandwidth limits are off. Fixtures are prepared outside timers and independently verified by the receiver; duplicate or missing delivery fails the run. Both sends and typed receipts must complete. Byte counters drain for 160 ms of quiet after the timer; the drain is excluded from throughput and included in bytes.

The desktop remained in normal use. Builds and correctness tests ran before timing. All samples, including scheduling outliers, are retained. Client CPU includes the proxy and excludes the server. Allocation records approximate process-wide allocated volume, rather than retained cache memory. This is unconstrained loopback without artificial bandwidth, latency or loss; it does not predict Internet performance or guarantee that compression never adds CPU work.

- Main correctness: **219 passed, one TypeScript-compiler-dependent case skipped**. Reliable UDP: **45 passed**, excluding the large-file category. Total **264 passed, one skipped**. [Main log](tests-main.log), [reliable log](tests-reliable.log), [TRX](tests/).
- WPF Release builds pass for net8.0-windows7.0 and net10.0-windows7.0, with one existing unused-field warning. [Build log](build-wpf.log).
- Browser: **47 adapter checks + 12 Portal checks**, including actual C# ↔ JavaScript SBC decoding/encoding. Permission, private caches, persistence failure and account isolation remain covered. Browser source is unchanged from the previously verified deployment. [Checks](browser-checks.log), [fixtures](browser-fixtures.json), [encoded frames](browser-encoded.json).
- **{verification["sourceFilesVerified"]} source hashes and {verification["dllFilesVerified"]} core DLL copies verified unchanged** after timing; every final series records the same core hash. [Source freeze](source-manifest.json), [DLL freeze](dll-freeze.json), [measured code and binaries](measured-source/), [verification](verification.json).
- Main: [raw](results.json), [analysis](analysis.md), [machine-readable statistics](analysis.json), [log](run.log).
- Sustained: [raw](sustained.json), [analysis](sustained-analysis.md), [statistics](sustained-analysis.json), [log](sustained.log).
- Adaptive: [raw](adaptive.json), [analysis](adaptive-analysis.md), [statistics](adaptive-analysis.json), [log](adaptive.log).
- Shared fanout: [raw](fanout.json), [log](fanout.log).

Reproduce from the repository root after building the two benchmark projects in Release (run each command sequentially):

```powershell
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-final/results.json 10 tcp repeated-1k,repeated-32k,delta-32k,delta-blocks-32k,random-32k,runs-32k,cold-first-32k
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-final/sustained.json 6 tcp sustained-delta-32k,sustained-delta-blocks-32k,sustained-random-32k
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-final/adaptive.json 5 tcp adaptive-32k,spaced-32k,marginal-32k
dotnet tools/BinaryCompression.FanoutBench/bin/Release/net8.0/BinaryCompression.FanoutBench.dll docs/binary-compression-benchmark/2026-10-15-tcp-final/fanout.json 5
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-final/results.json docs/binary-compression-benchmark/2026-10-15-tcp-final/analysis
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-final/sustained.json docs/binary-compression-benchmark/2026-10-15-tcp-final/sustained-analysis
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-final/adaptive.json docs/binary-compression-benchmark/2026-10-15-tcp-final/adaptive-analysis
```

The prior TCP/reliable-UDP results remain [here](../2026-10-15-final/README.md). Those earlier bytes and timings are not final-DLL measurements.
'''
(HERE / "README.md").write_text(text, encoding="utf-8")
print(f"Wrote {HERE / 'README.md'}")
