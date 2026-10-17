# Binary serializer performance

The complete measurements, confidence intervals, test outcomes, methodology and reproduction commands are in the [encoder testing README](encoder-testing/README.md).

For the separate adaptive network compression feature, see the [TCP compression on/off transfer benchmark](binary-compression-benchmark/2026-10-15-tcp-direct-plans/README.md). It measures actual socket transfers with private per-client caches, cold starts, partially changing objects and sustained traffic, counts dictionary updates, and includes a separate shared-client fanout case. Its speed, latency, and data-volume results are distinct from raw serializer timings. Earlier dated reports remain available for the original, reliable-UDP and earlier optimized implementations.

See [UDP_Reliable results](UDP_Reliable-results.md) for the separate transport comparison and its acceptance limits.
