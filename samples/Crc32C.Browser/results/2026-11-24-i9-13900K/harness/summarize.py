"""Summarizes Crc32C.Browser's runs into README.md's tables.

For each engine (node, chrome), bundle (interpreter, aot), input size and implementation: the
median of the runs' medians in MB/s, with the lowest and highest run; for each ratio line
(asm / .NET, asm / C#): the median of the runs' median ratios, with their range. Also prints
the machine load sampled before each run.

Usage: python summarize.py <folder with run<N>-<engine>-<bundle>.txt files>
"""
import glob
import os
import re
import statistics
import sys

NAMES = ("asm (crc32c.wasm.s)", ".NET BitOperations.Crc32C", "C# slicing-by-8")
SIZES = (64, 4096, 1048576)


def parse(folder):
    """Read every run file of a folder.

    Args:
        folder: The folder holding run<N>-<engine>-<bundle>.txt files.

    Returns:
        (throughput, ratios, loads): throughput maps (engine, bundle, size, name) to the runs'
        median MB/s; ratios maps (engine, bundle, size, rival) to the runs' median ratios; loads
        maps (run, engine, bundle) to the load percentage sampled before the run, or None.
    """
    throughput, ratios, loads = {}, {}, {}
    for path in sorted(glob.glob(os.path.join(folder, "run[0-9]-*.txt"))):
        match = re.match(r"run(\d)-(node|chrome)-(interpreter|aot)\.txt", os.path.basename(path))
        if not match:
            continue
        run, engine, bundle = match.groups()
        text = open(path, encoding="utf-8", errors="replace").read()
        load = re.search(r"machine load before: (\d+)%", text)
        loads[(run, engine, bundle)] = int(load.group(1)) if load else None
        size = None
        for line in text.splitlines():
            header = re.match(r"(\d+) bytes, 11 paired rounds", line)
            if header:
                size = int(header.group(1))
                continue
            row = re.match(r"\| (.+?) \| ([\d.]+) \| ([\d.]+) \| ([\d.]+) \|", line)
            if row and size and row.group(1) in NAMES:
                throughput.setdefault((engine, bundle, size, row.group(1)), []).append(float(row.group(2)))
                continue
            ratio = re.match(r"asm / (.+?): median ([\d.]+)x", line)
            if ratio and size:
                ratios.setdefault((engine, bundle, size, ratio.group(1)), []).append(float(ratio.group(2)))
    return throughput, ratios, loads


def main():
    """Print the load line and one throughput table and one ratio table per engine and bundle."""
    throughput, ratios, loads = parse(sys.argv[1])
    print("load before each run: " + ", ".join(f"run{r} {e} {b}: {v}%" for (r, e, b), v in sorted(loads.items())))
    for engine in ("node", "chrome"):
        for bundle in ("interpreter", "aot"):
            print(f"\n{engine}, C# {bundle}")
            for size in SIZES:
                for name in NAMES:
                    values = throughput.get((engine, bundle, size, name), [])
                    if values:
                        print(f"  {size:>7} B  {name:<27} {statistics.median(values):8.1f} MB/s"
                              f"  ({min(values):.1f} - {max(values):.1f}, {len(values)} runs)")
                for rival in NAMES[1:]:
                    values = ratios.get((engine, bundle, size, rival), [])
                    if values:
                        print(f"  {size:>7} B  asm / {rival:<27} {statistics.median(values):.2f}x"
                              f"  ({min(values):.2f}x - {max(values):.2f}x)")


if __name__ == "__main__":
    main()
