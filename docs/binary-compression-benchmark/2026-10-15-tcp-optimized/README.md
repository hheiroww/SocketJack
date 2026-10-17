# Intermediate TCP optimization measurements

This is the retained first iteration from the October 15 TCP work, measured with core SHA-256 `C422ECED0F297DB562E4156F2147923117426FB85F5B9E44CDD188B2F0D433F9`. The [subsequent final report](../2026-10-15-tcp-final/README.md) uses a different DLL. These samples are kept as evidence and are not pooled with the final results.

This iteration introduced bounded cold eligibility checks, cheaper sample hashing and ID reads, larger learned chunks, immediate peer-owned definitions, and direct deserialization of dictionary/literal slices. HTTP static-only caching was implemented and tested in this iteration too.

The ten-pair main matrix retained measurable costs: distributed changes had a warm paired speed ratio of 0.878 [0.833, 0.938], and fresh random traffic's cold ratio was 0.855 [0.704, 0.982]. Random warm timing did not establish a speed difference. Those results prompted the final verified-plan bypass, evidence-gated rotating cold scouts, and larger stream update batches. Results are unconstrained loopback measurements on a desktop in normal use.

- Main: [raw data](results.json), [all phase statistics](analysis.md), [machine-readable analysis](analysis.json), run log.
- Adaptive: [raw data](adaptive.json), [statistics](adaptive-analysis.md), log.
- Shared fanout: [raw data](fanout.json), log.
- Frozen 40 source files, five DLL copies, [measured source](measured-source/), and post-run verification.
- Correctness: 218 main tests passed with one skipped, plus 45 reliable-UDP tests. Browser checks: 47 adapter + 12 Portal, including actual C#/JavaScript interoperability. Final logs are `tests-main-complete.log`, `tests-reliable-complete.log` and `build-wpf-final.log`.

All timing series completed: 46,440 byte-verified objects across the main, adaptive and shared-fanout trials. Dictionary updates and acknowledgements are included in byte totals. Pilot and CPU-probe files are diagnostics and are excluded from these final intermediate statistics. Earlier failure logs remain diagnostic history; the final correctness counts come from the complete runs above.
