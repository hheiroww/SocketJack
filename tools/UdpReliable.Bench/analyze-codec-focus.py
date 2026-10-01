"""Analyze paired, alternating before/after codec-focus processes (stdlib only)."""
import argparse
import json
import random
import statistics
from pathlib import Path


def interval(before, after):
    rng = random.Random(7301)
    samples = []
    for _ in range(20000):
        indexes = [rng.randrange(len(before)) for _ in before]
        samples.append(sum(before[i] for i in indexes) / sum(after[i] for i in indexes))
    samples.sort()
    return [samples[500], samples[19499]]


def summarize(before, after):
    a = [row['Nanoseconds'] for row in before]
    b = [row['Nanoseconds'] for row in after]
    old_alloc = statistics.mean(row['Allocated'] for row in before)
    new_alloc = statistics.mean(row['Allocated'] for row in after)
    return dict(BeforeMicroseconds=statistics.mean(a) / 1000,
                AfterMicroseconds=statistics.mean(b) / 1000,
                Speedup=statistics.mean(a) / statistics.mean(b),
                Speedup95=interval(a, b),
                BeforeAllocated=old_alloc, AfterAllocated=new_alloc,
                AllocationReductionPercent=100 * (1 - new_alloc / old_alloc))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('directory', type=Path)
    parser.add_argument('--runs', type=int, default=10)
    args = parser.parse_args()
    groups = {}
    hashes = {}
    for variant in ('baseline', 'optimized'):
        rows = []
        cores, harnesses = set(), set()
        for run in range(args.runs):
            document = json.loads((args.directory / f'{variant}-{run}.json').read_text(encoding='utf-8-sig'))
            cores.add(document['CoreSha256']); harnesses.add(document['HarnessSha256'])
            current = document['Measurements']
            assert len(current) == 26 and all(row['Run'] == run and row['Count'] > 0 for row in current)
            rows.extend(current)
        assert len(cores) == len(harnesses) == 1, 'Binaries changed during measurements'
        hashes[variant] = dict(CoreSha256=next(iter(cores)), HarnessSha256=next(iter(harnesses)))
        for row in rows:
            key = (variant, row['Mode'], row['Payload'], row['Operation'])
            groups.setdefault(key, []).append(row)
        for key, items in groups.items():
            if key[0] == variant:
                items.sort(key=lambda row: row['Run'])
                assert [row['Run'] for row in items] == list(range(args.runs))
    assert hashes['baseline']['HarnessSha256'] == hashes['optimized']['HarnessSha256']
    comparisons = []
    for (variant, mode, payload, operation), before in sorted(groups.items()):
        if variant != 'baseline':
            continue
        after = groups[('optimized', mode, payload, operation)]
        if mode != 'segment':
            assert len({row['EncodedBytes'] for row in before + after}) == 1
        comparisons.append(dict(Mode=mode, Payload=payload, Operation=operation, Runs=args.runs,
                                **summarize(before, after)))
    versus_json = []
    for payload in (256, 32768, 262144):
        for operation in ('encode', 'decode', 'wrap-encode', 'decode-unwrap'):
            json_rows = groups[('optimized', 'json', payload, operation)]
            binary_rows = groups[('optimized', 'binary', payload, operation)]
            versus_json.append(dict(Payload=payload, Operation=operation,
                                    JsonEncodedBytes=json_rows[0]['EncodedBytes'], BinaryEncodedBytes=binary_rows[0]['EncodedBytes'],
                                    **summarize(json_rows, binary_rows)))
    result = dict(Method='Ratios of paired round means; 20000 paired bootstrap samples; seed 7301; percentile 95% intervals. Larger speedup is better.',
                  Runs=args.runs, Hashes=hashes, Comparisons=comparisons, BinaryVersusJson=versus_json)
    (args.directory / 'analysis.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    for row in comparisons:
        lo, hi = row['Speedup95']
        print(f"{row['Mode']:7} {row['Payload']:7} {row['Operation']:15} "
              f"{row['BeforeMicroseconds']:9.3f} -> {row['AfterMicroseconds']:9.3f} us "
              f"{row['Speedup']:.2f}x [{lo:.2f}, {hi:.2f}], allocation reduction {row['AllocationReductionPercent']:.1f}%")
    print('Optimized binary versus optimized SocketJack System.Text.Json:')
    for row in versus_json:
        lo, hi = row['Speedup95']
        print(f"{row['Payload']:7} {row['Operation']:15} {row['Speedup']:.2f}x [{lo:.2f}, {hi:.2f}]")


if __name__ == '__main__':
    main()
