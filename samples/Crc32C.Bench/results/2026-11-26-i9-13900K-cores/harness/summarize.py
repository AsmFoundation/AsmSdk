"""Pools the raw counts of several Crc32C.Bench --cores runs (run<N>-cores.json) into one summary.

Usage: python -I summarize.py <run1-cores.json> [<run2-cores.json> ...]

Prints, as Markdown:
  - each run's medians (to show run-to-run agreement),
  - the pooled medians over every round of every run, with the lowest and highest round,
  - the paired ratio asm/C# (per round, then the median), and how many rounds the assembly won,
  - the scaling from one core (per round: the N-core total over the same round's one-core total).

Every figure is derived from the JSON's per-thread counts and windows, not from the runs' own
printed summaries, so the pooled numbers can be re-derived by anyone from the committed files.
"""

import json
import statistics
import sys


def load(path):
    """Reads one run's JSON and returns (header, cells), cells keyed by (impl, size, cores, round)."""
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    cells = {}
    for c in doc["cells"]:
        # The rate is recomputed from the streams: each thread's calls over its own window, summed.
        rate = sum(s["calls"] / s["seconds"] for s in c["streams"])
        cells[(c["implementation"], c["size"], c["cores"], c["round"])] = rate
    return doc["header"], cells


def label(size):
    """64 B, 1 KiB, 1 MiB, as the docs write sizes."""
    if size >= 1 << 20 and size % (1 << 20) == 0:
        return f"{size >> 20} MiB"
    if size >= 1 << 10 and size % (1 << 10) == 0:
        return f"{size >> 10} KiB"
    return f"{size} B"


def fmt(v):
    """Three significant figures."""
    if v >= 100:
        return f"{v:.0f}"
    if v >= 10:
        return f"{v:.1f}"
    return f"{v:.2f}"


def time(ns):
    """ns, us or ms."""
    if ns >= 1e6:
        return fmt(ns / 1e6) + " ms"
    if ns >= 1e3:
        return fmt(ns / 1e3) + " us"
    return fmt(ns) + " ns"


def main(paths):
    runs = [load(p) for p in paths]
    header = runs[0][0]
    sizes = header["options"]["sizes"]
    cores = header["options"]["cores"]
    rounds = header["options"]["rounds"]
    impls = ["cSharp", "asm"]

    # Every (run, round) pair is one paired observation.
    def rates(impl, size, n):
        return [cells[(impl, size, n, r)] for _, cells in runs for r in range(1, rounds + 1)]

    print(f"## Each run's medians ({rounds} rounds each): GB/s, all cores together\n")
    print("| Size | Cores | " + " | ".join(f"run {i + 1} C# | run {i + 1} asm" for i in range(len(runs))) + " |")
    print("|---|---:|" + "---:|" * (2 * len(runs)))
    for size in sizes:
        for n in cores:
            row = [label(size), str(n)]
            for _, cells in runs:
                for impl in impls:
                    m = statistics.median(cells[(impl, size, n, r)] for r in range(1, rounds + 1))
                    row.append(fmt(m * size / 1e9))
            print("| " + " | ".join(row) + " |")

    total = len(runs) * rounds
    print(f"\n## Pooled: medians of all {total} rounds ({len(runs)} runs x {rounds})\n")
    print("GB/s and calls/s are all cores together; \"per call\" is one call's time on its core while the others run.\n")
    print("| Size | Cores | C# GB/s | asm GB/s | C# per call | asm per call | C# calls/s | asm calls/s | asm speed / C# | asm faster in |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for size in sizes:
        for n in cores:
            cs, asm = rates("cSharp", size, n), rates("asm", size, n)
            ratios = [a / c for a, c in zip(asm, cs)]
            mc, ma = statistics.median(cs), statistics.median(asm)
            print(
                f"| {label(size)} | {n} | {fmt(mc * size / 1e9)} | {fmt(ma * size / 1e9)} | "
                f"{time(n * 1e9 / mc)} | {time(n * 1e9 / ma)} | {fmt(mc / 1e6)}M | {fmt(ma / 1e6)}M | "
                f"{statistics.median(ratios):.2f}x | {sum(r > 1 for r in ratios)} of {len(ratios)} |"
            )

    print(f"\n## Pooled spread: the lowest and highest of the {total} rounds\n")
    print("| Size | Cores | C# GB/s | asm GB/s | asm speed / C# |")
    print("|---|---:|---:|---:|---:|")
    for size in sizes:
        for n in cores:
            cs, asm = rates("cSharp", size, n), rates("asm", size, n)
            ratios = [a / c for a, c in zip(asm, cs)]
            print(
                f"| {label(size)} | {n} | {fmt(min(cs) * size / 1e9)} to {fmt(max(cs) * size / 1e9)} | "
                f"{fmt(min(asm) * size / 1e9)} to {fmt(max(asm) * size / 1e9)} | {min(ratios):.2f}x to {max(ratios):.2f}x |"
            )

    more = [n for n in cores if n != 1]
    if 1 in cores and more:
        print("\n## Pooled scaling: N cores over 1 core, median of the per-round ratios (lowest to highest)\n")
        print("| Size | " + " | ".join(f"C# x{n} | asm x{n}" for n in more) + " |")
        print("|---|" + "---:|" * (2 * len(more)))
        for size in sizes:
            row = [label(size)]
            for n in more:
                for impl in impls:
                    s = [m / o for m, o in zip(rates(impl, size, n), rates(impl, size, 1))]
                    row.append(f"{statistics.median(s):.2f}x ({min(s):.2f} to {max(s):.2f})")
            print("| " + " | ".join(row) + " |")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])
