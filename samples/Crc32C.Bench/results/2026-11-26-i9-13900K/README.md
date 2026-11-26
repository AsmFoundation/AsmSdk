# Crc32C call benchmark, 2026-11-26, Intel Core i9-13900K: the generated binding

What a call costs through the C# binding the SDK writes from `crc32c.asm`'s `PROTO` line
(`Crc32c.Crc32c.Crc32cAppend`), next to the hand-written `[LibraryImport]` of the same function.
The question: do the generated bindings (phase 4 of AsmSdk's C# bindings) cost anything over the
imports a developer writes? `run1-call.md` to `run3-call.md` are BenchmarkDotNet's own reports of
three complete runs.

## Setup

- **Machine:** Intel Core i9-13900K (8 performance and 16 efficiency cores), Windows 11 25H2,
  natively (not in a container), .NET 10.0.1, RyuJIT x86-64-v3, the .NET SDK 11.0.100. Other
  sessions were working on the machine.
- **Pinned:** each run on logical CPU 2 (performance core 1) at High priority with Windows' power
  throttling (EcoQoS) off, while `coreguard` held the core's other hyperthread (CPU 3), so that
  no other thread shared the core: `harness/run-calls.ps1`, with `coreguard.exe` built from
  `samples/Crc32C.Browser/results/2026-11-24-i9-13900K/harness/coreguard.cs`.
- **Benchmark:** `samples/Crc32C.Bench` at the commit that added this folder, built in Release,
  BenchmarkDotNet 0.15.8, default job, in-process toolchain, `CallBenchmarks` only:
  `--filter *CallBenchmarks*`. Each benchmark calls `crc32c_append(0, null, 0)`: a call with
  nothing to checksum, so the figure is the call itself.
- **Three complete runs, back to back.** The table gives each run's mean and the median of the
  three.

## Results

| Call | Run 1 | Run 2 | Run 3 | Median |
|---|---:|---:|---:|---:|
| hand-written `[LibraryImport]`, with the GC transition (the baseline) | 7.609 ns | 7.060 ns | 7.355 ns | 7.355 ns |
| the generated binding (`PROTO` without `fast`: with the GC transition) | 7.383 ns | 7.420 ns | 6.797 ns | 7.383 ns |
| hand-written `[LibraryImport]` with `[SuppressGCTransition]` | 4.704 ns | 4.877 ns | 5.623 ns | 4.877 ns |

- **The generated binding costs what the hand-written import costs:** its ratio to the baseline
  was 0.97, 1.05 and 0.93 in the three runs, within each run's spread. Both are the same kind of
  call: the binding is a blittable `[DllImport]` extern, which is what `[LibraryImport]` emits
  for this signature (and this program has `DisableRuntimeMarshalling`, so neither marshals).
- **The GC transition** is the part worth choosing: about 2.5 ns of a 7.4 ns call. A `PROTO fast`
  line gives the binding `[SuppressGCTransition]`, for a function that is always short and never
  blocks. `crc32c_append` is not declared `fast`, since a 1 MiB input runs for tens of
  microseconds; `Crc32C.Net` keeps its two hand-written imports (without the transition up to
  4 KiB, with it above) and turns the generated bindings off.
- **Not comparable with 2026-11-19:** that run was in a Linux container (.NET 10.0.15); this one
  is native Windows, pinned. Only the ratios within one run are the result.
