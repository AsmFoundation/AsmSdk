# Crc32C.Browser benchmark, 2026-11-24, Intel Core i9-13900K

The numbers that [Crc32C, In WebAssembly](https://asmfoundation.github.io/AsmSdk/examples/crc32c.html#in-webassembly)
quotes. `run1-*.txt` to `run3-*.txt` are the twelve complete runs, as printed; `harness/` is
everything that took them.

## What is measured

`Crc32C.Browser bench` checksums the same buffer with three implementations, in the same
engine, after `verify` has checked that they agree:

| Name in the tables | What it is |
|---|---|
| asm (crc32c.wasm.s) | the Crc32C sample's WebAssembly half, linked into the app's .NET runtime and called through `Crc32C.Net`'s `[LibraryImport]`: slicing-by-8, eight 256-entry tables, 8 bytes per step |
| .NET BitOperations.Crc32C | .NET's own CRC-32C, 8 bytes per call, then a byte at a time. WebAssembly has no CRC instruction, so it runs its software fallback, one 256-entry table read once per byte (`BitOperations+Crc32Fallback`, whose only table is a `UInt32[256]`) |
| C# slicing-by-8 | the same algorithm as the asm, written in C#: the rival that separates the code from the method |

For each size (64 B, 4 KiB, 1 MiB): a calibration finds how many calls fill about 60 ms for each
implementation, then 11 paired rounds run all three over the buffer, in an order that rotates
every round. Each run reports each implementation's median MB/s (10^6 bytes per second) over its
11 rounds, and the median of the per-round ratios.

Each configuration ran three times. The figures below are the median of the three runs'
medians; the range is the lowest and the highest run.

## Setup

- **Machine:** Intel Core i9-13900K (8 performance cores with two hyperthreads each, 16
  efficiency cores), Windows 11.
- **Engines:** Node 22.14.0 (V8 12.4.254.21), and headless Chrome 156.0.8078.82 (V8 15.6.75.27).
- **The app:** `samples/Crc32C.Browser` at the commit that added this folder, published with the
  .NET SDK 10.0.404 and the `wasm-tools` workload (.NET 10.0.15), twice:
  - `dotnet publish -c Release`: the C# runs on Mono's interpreter (a .NET WebAssembly app's
    default; the jiterpreter compiles its hot loops to WebAssembly as it runs);
  - `dotnet publish -c Release -p:RunAOTCompilation=true`: the C# is compiled ahead of time to
    WebAssembly. Its `dotnet.native.wasm` is 7.27 MB, the interpreter's 2.88 MB.

  Both say `debugLevel: 0`. A build's bundle says -1, and in Chrome that starts .NET's debugger,
  which keeps the interpreter unoptimized: see the tip in the Crc32C example.
- **One core, nothing else on it.** Every run was pinned to logical CPU 2 (performance core 1's
  first hyperthread) at High priority, with Windows' power throttling (EcoQoS) off. Meanwhile
  `coreguard` held CPU 3, the same core's other hyperthread, at High priority, with `pause`
  instructions that leave the core's execution units to CPU 2. In Chrome, every Chrome process
  was pinned the same way after the page had started the .NET runtime and before the benchmark
  started. CPU 2 and 3 were confirmed as one performance core with `coreguard topology`.
- **Other work was running.** The machine's load before each run was 66 to 88% for runs 1 and 2,
  and 3 to 64% for run 3 (each file's first line). The three runs of each configuration agree
  within 2% (most within 0.5%), loaded or not.

## Results

**MB/s, median of three runs.** Larger is faster.

| Engine, C# | Size | asm (crc32c.wasm.s) | .NET BitOperations.Crc32C | C# slicing-by-8 |
|---|---:|---:|---:|---:|
| Node, interpreted | 64 B | **256.7** | 148.7 | 179.6 |
| Node, interpreted | 4 KiB | **1188.4** | 189.5 | 241.7 |
| Node, interpreted | 1 MiB | **1236.9** | 185.8 | 238.6 |
| Node, AOT | 64 B | 639.2 | 246.2 | **774.0** |
| Node, AOT | 4 KiB | 1233.3 | 279.4 | **1259.2** |
| Node, AOT | 1 MiB | 1236.0 | 279.5 | **1266.8** |
| Chrome, interpreted | 64 B | **313.3** | 66.7 | 131.7 |
| Chrome, interpreted | 4 KiB | **1185.1** | 73.4 | 158.0 |
| Chrome, interpreted | 1 MiB | **1247.1** | 72.8 | 156.5 |
| Chrome, AOT | 64 B | **787.9** | 252.4 | 736.7 |
| Chrome, AOT | 4 KiB | **1259.7** | 289.2 | 1173.2 |
| Chrome, AOT | 1 MiB | **1248.7** | 289.8 | 1180.7 |

**The asm's speed over each rival,** the median of the runs' median per-round ratios (lowest
and highest run in brackets):

| Engine, C# | Size | asm / .NET BitOperations.Crc32C | asm / C# slicing-by-8 |
|---|---:|---:|---:|
| Node, interpreted | 64 B | 1.73x (1.71-1.74) | 1.43x (1.43-1.43) |
| Node, interpreted | 4 KiB | 6.26x (6.20-6.30) | 4.92x (4.90-4.92) |
| Node, interpreted | 1 MiB | 6.66x (6.64-6.67) | 5.18x (5.17-5.19) |
| Node, AOT | 64 B | 2.59x (2.59-2.60) | 0.82x (0.82-0.83) |
| Node, AOT | 4 KiB | 4.41x (4.39-4.41) | 0.98x (0.97-0.98) |
| Node, AOT | 1 MiB | 4.42x (4.42-4.43) | 0.98x (0.98-0.98) |
| Chrome, interpreted | 64 B | 4.68x (4.66-4.70) | 2.38x (2.38-2.42) |
| Chrome, interpreted | 4 KiB | 16.19x (16.18-16.21) | 7.51x (7.49-7.52) |
| Chrome, interpreted | 1 MiB | 17.12x (17.01-17.17) | 7.97x (7.93-7.98) |
| Chrome, AOT | 64 B | 3.13x (3.12-3.13) | 1.07x (1.07-1.08) |
| Chrome, AOT | 4 KiB | 4.36x (4.35-4.41) | 1.07x (1.07-1.09) |
| Chrome, AOT | 1 MiB | 4.32x (4.32-4.33) | 1.06x (1.06-1.06) |

## What the numbers say

- **The asm runs at 1.19 to 1.26 GB/s on 4 KiB and more, in both engines and both builds.** It
  is the same WebAssembly whatever the C# around it does, and V8 compiles it the same way in
  Node and in Chrome.
- **Against .NET's own CRC-32C, from 4 KiB up: 4.3 to 4.4 times faster when the C# is compiled
  ahead of time, and 6.3 to 17 times when it is interpreted.** .NET has no CRC instruction to use
  on WebAssembly and falls back to one table, read once per byte; slicing-by-8 makes eight
  independent reads per 8 bytes, which the engine overlaps.
- **Against the same algorithm in C#, from 4 KiB up: 0.98 to 1.07 times when the C# is compiled
  ahead of time.** With equal algorithms, the hand-written WebAssembly is no faster than what
  .NET's AOT compiler makes of the C#: the 4.4x over `BitOperations.Crc32C` comes from the
  method, not from writing it by hand. Interpreted, the C# runs 4.9 to 8.0 times slower than the
  asm, so an app on .NET's default, the interpreter, gains the most from the library.
- **Small inputs pay the call.** At 64 bytes the call into the library weighs as much as the
  work. Called from interpreted C#, the asm reaches 256.7 MB/s in Node, a fifth of its
  large-input speed, and 639.2 MB/s from AOT-compiled C#. AOT-compiled C# slicing-by-8 crosses no
  boundary at all, and is faster than the library in Node at 64 bytes (the asm 0.82x).
- **The interpreted C# is faster in Node than in Chrome** (238.6 against 156.5 MB/s for the C#
  slicing-by-8 at 1 MiB), with the same bundle and the same jiterpreter options. The engines'
  V8 versions differ (12.4 and 15.6); the cause was not investigated. From 4 KiB up, the asm and
  the AOT-compiled C# slicing-by-8 are within 7% of each other in both engines.

## Reproduce

On Windows with Git Bash, Node 22, Python 3, Chrome and the .NET SDK with the `wasm-tools`
workload:

```sh
cd samples/Crc32C.Browser
mkdir -p /tmp/crc
dotnet publish -c Release && cp -r bin/Release/net10.0/browser-wasm/AppBundle /tmp/crc/interpreter
dotnet publish -c Release -p:RunAOTCompilation=true && cp -r bin/Release/net10.0/browser-wasm/AppBundle /tmp/crc/aot
dotnet build results/2026-11-24-i9-13900K/harness/coreguard.cs -c Release -o /tmp/crc/coreguard
results/2026-11-24-i9-13900K/harness/run-all.sh /tmp/crc /tmp/crc/coreguard/coreguard.exe /tmp/crc/out
python results/2026-11-24-i9-13900K/harness/summarize.py /tmp/crc/out
```

| File | Does |
|---|---|
| `harness/coreguard.cs` | `topology` lists each core's hyperthreads; `spin <s>` holds a CPU with `pause` |
| `harness/run-node.ps1` | one Node run, pinned, with the guard |
| `harness/run-chrome.ps1` | one Chrome run, with the guard, through `bench-chrome.mjs` |
| `harness/bench-chrome.mjs`, `harness/bench.html` | serve a bundle copy, start headless Chrome, pin it (`harness/pin-chrome.ps1`) once .NET has started, run `bench`, print its output |
| `harness/run-all.sh` | the twelve runs, interleaved |
| `harness/summarize.py` | the tables above, from the run files |

On another CPU, take the timed CPU and its sibling from `coreguard topology` (`-Cpu` and
`-Sibling` on the scripts).
