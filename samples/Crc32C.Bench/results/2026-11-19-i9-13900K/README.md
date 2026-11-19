# Crc32C benchmark, 2026-11-19, Intel Core i9-13900K

The numbers that [Testing and measuring from C#](https://asmfoundation.github.io/AsmSdk/examples/testing-and-benchmarks.html)
and the article [C# and assembly, the easy way](https://asmfoundation.github.io/AsmSdk/articles/csharp-and-assembly.html)
quote. `run1-*.md` to `run3-*.md` are BenchmarkDotNet's own reports of three complete runs.

## Setup

- **Machine:** Intel Core i9-13900K (8 performance and 16 efficiency cores), Windows 11. Other
  work was running: the host was about 40% busy before the runs.
- **Where the benchmark ran:** `mcr.microsoft.com/dotnet/runtime:10.0` (Ubuntu 24.04) in Docker
  Desktop's WSL2 virtual machine, .NET 10.0.15, RyuJIT x86-64-v3.
- **Benchmark:** `samples/Crc32C.Bench` at the commit that added this folder, built in Release,
  BenchmarkDotNet 0.15.8, default job, in-process toolchain:

  ```sh
  docker run --rm -v <bin/Release/net10.0>:/app:ro -v <out>:/out -w /tmp mcr.microsoft.com/dotnet/runtime:10.0 dotnet /app/Crc32C.Bench.dll --filter '*' --artifacts /out/bdn
  ```

- **Three complete runs, back to back.** Each figure below is the median of the three runs'
  means; the range is the lowest and highest mean. GB/s is the size divided by the median.

## CRC-32C

| Size | Code | Median | Lowest - highest | GB/s |
|---|---|---:|---:|---:|
| 64 B | C# BitOperations.Crc32C | 3.35 ns | 3.21 ns - 3.77 ns | 19.12 |
| 64 B | asm crc32c_append (dispatcher) | 13.04 ns | 12.02 ns - 13.21 ns | 4.91 |
| 64 B | asm sse42 3-way | 14.28 ns | 12.48 ns - 14.33 ns | 4.48 |
| 64 B | asm sse42 1 chain | 13.70 ns | 12.34 ns - 13.95 ns | 4.67 |
| 64 B | asm table | 100.94 ns | 99.88 ns - 103.84 ns | 0.63 |
| 256 B | C# BitOperations.Crc32C | 21.21 ns | 20.90 ns - 22.26 ns | 12.07 |
| 256 B | asm crc32c_append (dispatcher) | 18.23 ns | 17.02 ns - 18.54 ns | 14.04 |
| 256 B | asm sse42 3-way | 18.29 ns | 17.28 ns - 18.53 ns | 14.00 |
| 256 B | asm sse42 1 chain | 34.97 ns | 33.64 ns - 35.51 ns | 7.32 |
| 256 B | asm table | 550.39 ns | 549.17 ns - 561.27 ns | 0.47 |
| 1 KiB | C# BitOperations.Crc32C | 144.28 ns | 142.29 ns - 147.73 ns | 7.10 |
| 1 KiB | asm crc32c_append (dispatcher) | 46.26 ns | 45.98 ns - 48.67 ns | 22.14 |
| 1 KiB | asm sse42 3-way | 50.06 ns | 49.45 ns - 50.65 ns | 20.45 |
| 1 KiB | asm sse42 1 chain | 85.53 ns | 82.74 ns - 88.94 ns | 11.97 |
| 1 KiB | asm table | 2.35 μs | 2.35 μs - 2.35 μs | 0.44 |
| 64 KiB | C# BitOperations.Crc32C | 11.01 μs | 10.97 μs - 11.06 μs | 5.95 |
| 64 KiB | asm crc32c_append (dispatcher) | 2.93 μs | 2.92 μs - 2.95 μs | 22.35 |
| 64 KiB | asm sse42 3-way | 2.93 μs | 2.93 μs - 2.93 μs | 22.39 |
| 64 KiB | asm sse42 1 chain | 8.33 μs | 8.15 μs - 8.36 μs | 7.86 |
| 64 KiB | asm table | 153.67 μs | 152.55 μs - 155.30 μs | 0.43 |
| 1 MiB | C# BitOperations.Crc32C | 177.65 μs | 176.91 μs - 179.75 μs | 5.90 |
| 1 MiB | asm crc32c_append (dispatcher) | 48.50 μs | 47.15 μs - 48.82 μs | 21.62 |
| 1 MiB | asm sse42 3-way | 48.10 μs | 47.11 μs - 48.22 μs | 21.80 |
| 1 MiB | asm sse42 1 chain | 132.42 μs | 132.19 μs - 136.35 μs | 7.92 |
| 1 MiB | asm table | 2.47 ms | 2.45 ms - 2.48 ms | 0.42 |

| Call (empty input) | Median | Lowest - highest |
|---|---:|---:|
| call, with the GC transition | 9.60 ns | 8.21 ns - 10.06 ns |
| call, SuppressGCTransition | 3.38 ns | 3.29 ns - 4.15 ns |

## What the numbers say

- **Large inputs: 3.7 times the C# loop.** At 1 MiB the 3-way kernel checksums 21.8 GB/s,
  the C# loop 5.90 GB/s (3.69x), the assembly single chain 7.92 GB/s (the kernel is 2.75x
  that). At 64 KiB: 3.76x the C# loop.
- **The single chain beats the C# loop with the same instruction, 1.34x.** The JIT's loop is
  `mov eax, eax` then `crc32 rax, qword ptr [rdi]`: it zero-extends the `uint` checksum before
  each 64-bit `crc32`, and that move sits in the chain of dependent steps, so each 8 bytes take
  4 cycles instead of 3. 4/3 = 1.33. (`DOTNET_JitDisasm=ManagedCrc32C` shows the loop.)
- **The cycle counts agree:** 8 bytes per 3 cycles (7.92 GB/s), per 4 cycles (5.90 GB/s) and
  about 7.3 per cycle (21.8 GB/s) all fit one clock of about 3 GHz; the kernel's ideal is 8
  bytes per cycle, less the merge and lane 0's wait for it.
- **Small inputs: the call costs as much as the work.** An empty call takes 3.38 ns without the
  GC transition and 9.60 ns with it. At 64 bytes the C# loop, inlined into the benchmark,
  finishes in 3.35 ns and successive calls overlap in the CPU; the library takes 13 ns. From 256
  bytes on, the assembly is faster: 1.16x at 256 B, 3.1x at 1 KiB.
- **SuppressGCTransition saves 6.2 ns on an empty call.** With real work, part of the transition
  overlaps the work, and the measured difference between the dispatcher (no transition up to
  4 KiB) and the per-path calls (with it) is 0 to 4 ns.
- **The table path, 0.42 GB/s,** is what a CPU without SSE4.2 gets: about 50 times slower than
  the 3-way kernel. The dispatcher exists so that no CPU that has the instructions runs it.
- **Noise:** the hybrid CPU and the virtual machine move the benchmark between core types; the
  64-byte cases vary most (up to 17% between runs), the 64 KiB and 1 MiB cases least (within
  4%).
