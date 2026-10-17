# Previous versus optimized binary compression

Absolute previous/optimized medians are sequential experiments, not paired samples. Paired on/off estimates and intervals are retained separately within each experiment. Useful workloads must match.

Both experiments use the same deterministic fixtures, process isolation, phase counts, window sizes, transport settings, and socket-byte accounting. The optimized harness additionally records managed allocations and adaptive counters outside timing. Absolute throughput can vary between the two sessions; the new on/off comparison is the primary evidence for current overhead.

## Warm throughput

| Transport | Workload | Previous off/on MiB/s | Optimized off/on MiB/s | Previous paired on/off [95% CI] | Optimized paired on/off [95% CI] |
|---|---|---:|---:|---|---|
| reliable-udp | delta-32k | 18.49 / 11.32 | 21.36 / 22.07 | 0.604× [0.565, 0.635] | 1.030× [0.955, 1.128] |
| reliable-udp | random-32k | 17.00 / 11.20 | 20.84 / 21.51 | 0.653× [0.568, 0.760] | 1.006× [0.969, 1.045] |
| reliable-udp | repeated-1k | 0.71 / 0.67 | 0.84 / 0.81 | 0.913× [0.800, 1.015] | 1.041× [0.949, 1.228] |
| reliable-udp | repeated-32k | 17.63 / 12.37 | 21.24 / 23.59 | 0.711× [0.649, 0.772] | 1.134× [1.099, 1.172] |
| reliable-udp | runs-32k | 18.24 / 12.79 | 21.40 / 26.37 | 0.655× [0.532, 0.757] | 1.233× [1.198, 1.265] |
| tcp | delta-32k | 24.85 / 12.42 | 28.50 / 23.83 | 0.517× [0.484, 0.552] | 0.878× [0.791, 1.004] |
| tcp | random-32k | 26.92 / 12.50 | 29.28 / 28.42 | 0.484× [0.442, 0.528] | 0.981× [0.958, 1.008] |
| tcp | repeated-1k | 0.82 / 0.76 | 0.89 / 0.87 | 0.947× [0.881, 1.040] | 0.967× [0.943, 0.989] |
| tcp | repeated-32k | 25.03 / 12.64 | 28.41 / 25.68 | 0.496× [0.448, 0.540] | 0.906× [0.880, 0.930] |
| tcp | runs-32k | 25.40 / 14.55 | 28.88 / 28.92 | 0.552× [0.512, 0.587] | 0.993× [0.966, 1.019] |

## Complete connection bytes

Includes setup, training, updates, acknowledgements, all phases and interphase traffic in both directions.

| Transport | Workload | Previous off/on bytes | Optimized off/on bytes | Previous saved | Optimized saved |
|---|---|---:|---:|---:|---:|
| reliable-udp | delta-32k | 8,561,679 / 2,709,852 | 8,561,561 / 2,741,639 | 68.35% | 67.98% |
| reliable-udp | random-32k | 8,561,667 / 8,561,729 | 8,561,525 / 8,561,610 | -0.00% | -0.00% |
| reliable-udp | repeated-1k | 316,832 / 129,916 | 316,832 / 130,756 | 59.00% | 58.73% |
| reliable-udp | repeated-32k | 8,561,701 / 677,691 | 8,561,594 / 708,012 | 92.10% | 91.73% |
| reliable-udp | runs-32k | 8,561,725 / 87,479 | 8,561,548 / 76,174 | 98.98% | 99.11% |
| tcp | delta-32k | 8,433,968 / 2,546,681 | 8,433,968 / 2,575,915 | 69.80% | 69.46% |
| tcp | random-32k | 8,433,968 / 8,434,018 | 8,433,968 / 8,434,018 | -0.00% | -0.00% |
| tcp | repeated-1k | 307,248 / 119,164 | 307,248 / 121,141 | 61.22% | 60.57% |
| tcp | repeated-32k | 8,433,968 / 489,520 | 8,433,968 / 655,539 | 94.20% | 92.23% |
| tcp | runs-32k | 8,433,968 / 75,353 | 8,433,968 / 66,590 | 99.11% | 99.21% |
