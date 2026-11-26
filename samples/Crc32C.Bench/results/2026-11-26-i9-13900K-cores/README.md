# Crc32C on one core and on four, 2026-11-26, Intel Core i9-13900K

The C# loop and the assembly library, each on one performance core and on four at once, timed
by `Crc32C.Bench --cores` in paired rounds. BenchmarkDotNet times one call on one thread; this
answers what it cannot: does the assembly's lead hold when every core checksums at once?

- `run1-cores.md` to `run3-cores.md`: the program's own reports of three complete runs.
- `run1-cores.json` to `run3-cores.json`: every round's raw counts (each thread's checksums and
  its own measure of its window).
- `harness/summarize.py` pools the three runs into the tables below:
  `python -I harness/summarize.py run1-cores.json run2-cores.json run3-cores.json`.
- `cross-check/`: BenchmarkDotNet on the same core, the JIT's log, and the clock it ran at.

## Setup

- **Machine:** Intel Core i9-13900K (8 performance cores with two hyperthreads each, 16
  efficiency cores), Windows 11 25H2, natively (not in a container), .NET 10.0.1, RyuJIT
  x86-64-v3, the .NET SDK 11.0.100. Other sessions were working on the machine: 16 to 32% of its
  CPU was busy before the runs.
- **The library:** the runs used `crc32c.dll` built by AsmFoundation.Sdk 0.3.0. The 0.4.0 build
  at the commit that added this folder is byte for byte the same in every section (`.text`,
  `.rdata`, `.data`, `.reloc`): 0.4.0 added only the sources' `PROTO` lines.
- **The clock:** the timed cores ran at 2.9 GHz, not at the CPU's 5.8 GHz turbo. Windows'
  processor counters read 2,899 to 2,900 MHz in all 90 samples (every 5 seconds) taken during
  the cross-checks below (`cross-check/clock-*.csv`). In every run, the C# loop's 5.74 GB/s at
  1 MiB is what its 4 cycles per 8 bytes give at 2.87 GHz. Absolute figures are at that clock;
  the ratios do not depend on it.
- **Pinned:** `harness/run-cores.ps1` ran `Crc32C.Bench --cores 1,4 --cpus 2,4,6,8` at High
  priority. The program pinned its timed threads to logical CPUs 2, 4, 6 and 8, the first
  hyperthreads of performance cores 1 to 4 (core 0 keeps the interrupts Windows sends it), and
  turned power throttling (EcoQoS) off for itself. One `coreguard` per core held its other
  hyperthread (CPUs 3, 5, 7 and 9) at High priority, so that no other thread shared a timed
  core. `coreguard.exe` is built from
  `samples/Crc32C.Browser/results/2026-11-24-i9-13900K/harness/coreguard.cs`.
- **A round** times every cell once: the C# loop or the assembly, at 64 B, 256 B, 1 KiB, 64 KiB
  and 1 MiB, on 1 or 4 cores. Each cell runs 0.1 s of warm-up, then a 0.5 s window. The two
  implementations alternate which goes first from cell to cell and from round to round.
  - **N cores:** N threads, each checksumming its own copy of the input. The cell's figure is
    their total.
  - **Warm-up:** before round 1, every implementation runs at every size until tiered
    compilation has promoted the timed loops and the C# path into the library; the JIT's log
    confirmed it.
  - **Checks:** every thread compares its checksum with the managed one before its window and
    after it.
- **The two sides:**
  - **The C# loop:** `BitOperations.Crc32C` over 8-byte words (`Crc32CBenchmarks.ManagedCrc32C`),
    which the JIT inlines into the timed loop.
  - **The assembly:** `crc32c_append` through `Crc32C.Net`'s `HashToUInt32`, the call a C#
    caller makes (without the GC transition up to 4 KiB). The dispatcher picks the 3-way path on
    this CPU.
- **Three complete runs,** back to back, started at 05:18, 05:37 and 05:39 UTC: 12 rounds each,
  36 in all.

## Results

Medians of all 36 rounds. GB/s is all cores together; "per call" is one call's time on its core
while the others run; the ratio is the median of the 36 per-round ratios.

| Size | Cores | C# loop | Assembly | C# per call | Assembly per call | Assembly ÷ C# | Assembly faster in |
|---|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 1 | 12.0 GB/s | 7.34 GB/s | 5.34 ns | 8.72 ns | 0.61× | 0 of 36 |
| 64 B | 4 | 47.9 GB/s | 29.5 GB/s | 5.34 ns | 8.67 ns | 0.61× | 0 of 36 |
| 256 B | 1 | 9.31 GB/s | 19.1 GB/s | 27.5 ns | 13.4 ns | 2.04× | 36 of 36 |
| 256 B | 4 | 37.2 GB/s | 76.4 GB/s | 27.5 ns | 13.4 ns | 2.05× | 36 of 36 |
| 1 KiB | 1 | 6.78 GB/s | 19.6 GB/s | 151 ns | 52.4 ns | 2.88× | 36 of 36 |
| 1 KiB | 4 | 27.2 GB/s | 78.3 GB/s | 151 ns | 52.3 ns | 2.88× | 36 of 36 |
| 64 KiB | 1 | 5.76 GB/s | 20.1 GB/s | 11.4 µs | 3.26 µs | 3.49× | 36 of 36 |
| 64 KiB | 4 | 23.1 GB/s | 80.4 GB/s | 11.4 µs | 3.26 µs | 3.49× | 36 of 36 |
| 1 MiB | 1 | 5.74 GB/s | 20.0 GB/s | 183 µs | 52.3 µs | 3.49× | 36 of 36 |
| 1 MiB | 4 | 23.0 GB/s | 79.9 GB/s | 183 µs | 52.5 µs | 3.48× | 36 of 36 |

At 64 B, in checksums per second: the C# loop 187 million on one core and 749 million on four,
the assembly 115 million and 461 million.

**Four cores over one** (each round's four-core total over the same round's one-core total;
median, then the lowest and the highest round):

| Size | C# loop | Assembly |
|---|---:|---:|
| 64 B | 4.00× (3.72 to 4.09) | 4.02× (3.91 to 4.13) |
| 256 B | 4.00× (3.90 to 4.07) | 4.00× (3.96 to 4.11) |
| 1 KiB | 4.00× (3.97 to 4.03) | 4.00× (3.95 to 4.13) |
| 64 KiB | 4.00× (3.98 to 4.03) | 4.00× (3.97 to 4.04) |
| 1 MiB | 4.00× (3.97 to 4.03) | 3.99× (3.95 to 4.08) |

**Each run's medians** (GB/s, all cores together):

| Size | Cores | Run 1 C# | Run 1 asm | Run 2 C# | Run 2 asm | Run 3 C# | Run 3 asm |
|---|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 1 | 12.0 | 7.29 | 12.1 | 7.34 | 9.83 | 7.44 |
| 64 B | 4 | 48.0 | 29.4 | 48.5 | 29.5 | 37.9 | 29.9 |
| 256 B | 1 | 9.32 | 19.0 | 9.33 | 19.1 | 9.27 | 19.2 |
| 256 B | 4 | 37.3 | 76.1 | 37.4 | 76.4 | 36.5 | 76.9 |
| 1 KiB | 1 | 6.80 | 19.6 | 6.78 | 19.5 | 6.75 | 19.6 |
| 1 KiB | 4 | 27.2 | 78.5 | 27.2 | 78.2 | 27.0 | 78.2 |
| 64 KiB | 1 | 5.76 | 20.1 | 5.76 | 20.1 | 5.76 | 20.1 |
| 64 KiB | 4 | 23.1 | 80.4 | 23.1 | 80.4 | 23.0 | 80.3 |
| 1 MiB | 1 | 5.74 | 20.0 | 5.74 | 20.0 | 5.74 | 20.1 |
| 1 MiB | 4 | 22.9 | 80.0 | 23.0 | 80.0 | 22.9 | 79.4 |

**What they show:**

- **Four cores do four times the work, on both sides.** At every size, both totals grow 4.00×,
  because each thread's input stays in its own core's caches (1 MiB fits each performance core's
  2 MB L2) and the threads share nothing. So the assembly's lead is the same on four cores as on
  one: 3.49× at 64 KiB and 1 MiB, 2.88× at 1 KiB, 2.04× at 256 B.
- **At 64 B the C# loop still wins, by 1.6×, on four cores as on one.** The assembly pays the
  call from C# into native code on every checksum, and at 64 B that costs about as much as the
  checksum itself. Below 192 bytes the library also runs a single `crc32` chain, the same
  instruction the C# loop uses.
- **The runs agree** within 2.5% at every size and core count, except the C# loop at 64 B. That
  cell gave 12.0 and 12.1 GB/s in runs 1 and 2 and 9.83 in run 3, a change from one process to
  the next and steady within each; the pooled median keeps 12.0. The assembly at 64 B moved by
  2%.
- **The guards matter.** A short check run after these runs, without `coreguard` on the
  sibling hyperthreads, saw the four-core 1 MiB cells fall to between 29.5 and 81.4 GB/s for
  the assembly and between 12.9 and 21.3 for the C# loop: other processes' threads shared the
  timed cores. With the guards, no round fell below 77.9 and 22.8 GB/s.

## Cross-check: BenchmarkDotNet on the same core

`harness/run-bdn.ps1` ran BenchmarkDotNet's `Crc32CBenchmarks`, the C# loop and the dispatcher
(`--filter *Crc32CBenchmarks.Managed* *Crc32CBenchmarks.Dispatcher*`). It ran natively on one
core: the process pinned to CPU 2 at High priority, power throttling off, and `coreguard` on
CPU 3. It ran twice: with tiered compilation on, .NET's default (`cross-check/bdn-tiered.md`),
and off, with `DOTNET_TieredCompilation=0` in the environment (`cross-check/bdn-not-tiered.md`).

| Size | C# loop, tiered | Assembly, tiered | C# loop, not tiered | Assembly, not tiered | This folder's run, 1 core |
|---|---:|---:|---:|---:|---:|
| 64 B | 4.39 ns | 24.2 ns | 5.35 ns | 9.92 ns | 5.34 and 8.72 ns |
| 256 B | 28.1 ns | 39.5 ns | 28.4 ns | 15.6 ns | 27.5 and 13.4 ns |
| 1 KiB | 147 ns | 54.7 ns | 150 ns | 54.9 ns | 151 and 52.4 ns |
| 64 KiB | 11.4 µs | 3.26 µs | 11.4 µs | 3.27 µs | 11.4 and 3.26 µs |
| 1 MiB | 183 µs | 52.7 µs | 183 µs | 52.8 µs | 183 and 52.3 µs |

- **From 1 KiB up, BenchmarkDotNet and this folder's run agree** within 5%, tiered or not.
- **Below that, BenchmarkDotNet with tiered compilation on times the assembly 2.4 to 2.5 times
  slower than with it off** (24.2 against 9.92 ns at 64 B, 39.5 against 15.6 ns at 256 B). The
  JIT's log shows why. A third run timed the dispatcher alone with `DOTNET_JitDisasmSummary=1`
  (`cross-check/bdn-tiered-jit-log.md`; the log's relevant lines are in
  `cross-check/jit-tiers.txt`):
  - the C# methods between BenchmarkDotNet and the library (`Dispatcher()`,
    `Crc32C.HashToUInt32` and `Crc32C.Append`) were compiled Tier-0 before the 64 B case and
    recompiled as instrumented Tier-0 during the 256 B case: unoptimized code both times;
  - they reached Tier-1 only during the 64 KiB case;
  - so that run timed the dispatcher at 24.3 ns, 39.5 ns and 90.9 ns at 64 B, 256 B and 1 KiB.
    How many sizes the slow glue reaches depends on when tiering catches up: in the first run,
    1 KiB already matched the untiered figure.
- **The C# loop escapes it:** on-stack replacement compiles its loop inside the call. Tiered,
  it even runs faster than untiered (4.39 against 5.35 ns at 64 B).
- **So the 2026-11-19 figures at 64 B and 256 B most likely include unoptimized glue too**
  (13.0 and 18.2 ns for the dispatcher, from BenchmarkDotNet in a Linux container with tiered
  compilation on). That run was not repeated in its container here, so by how much is not
  measured.

## Not comparable

- **With 2026-11-19:** that run was in a Linux container with unpinned threads, and this one is
  native Windows, pinned. Only ratios within one run are results.
- **With the generated-binding run in `../2026-11-26-i9-13900K`:** that run pinned the whole
  process to one core and timed calls with nothing to checksum.
