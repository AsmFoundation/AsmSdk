# Testing and measuring from C#

An assembly library is tested and measured like any .NET code: with an xunit project and a
BenchmarkDotNet project that call it through its C# API. This page shows both for
[Crc32C](crc32c.md): [`samples/Crc32C.Tests`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Tests)
and [`samples/Crc32C.Bench`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Bench).

## The tests

The test project references the C# wrapper, which references the `.asmproj`, so building the
tests builds the assembly too, for each of its eight native RIDs, under `runtimes/<rid>/native/` next
to the test assembly. The same test output runs on any of those platforms, with that
platform's library.

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <OutputType>Exe</OutputType>
  <IsTestProject>true</IsTestProject>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="4.0.3" />
  <ProjectReference Include="../Crc32C.Net/Crc32C.Net.csproj" />
</ItemGroup>
```

**Each path is tested on its own.** A theory runs once per path the CPU supports, through the
wrapper's per-path overload, so the table, the single chain and the 3-way kernel are all
checked on every machine that can run them, not only the one the dispatcher picks:

```csharp
public static TheoryData<Crc32CPath> Paths
{
    get
    {
        var paths = new TheoryData<Crc32CPath>();
        foreach (Crc32CPath path in Enum.GetValues<Crc32CPath>().Where(Crc32C.IsSupported))
        {
            paths.Add(path);
        }

        return paths;
    }
}
```

**The reference shares nothing with the code under test.** It is CRC-32C's definition, one bit
at a time, in C#: no table and no `crc32` instruction, so a mistake in the assembly's table,
constants or instruction use cannot cancel out.

```csharp
uint register = ~crc;
foreach (byte b in data)
{
    register ^= b;
    for (int bit = 0; bit < 8; bit++)
    {
        register = (register >> 1) ^ (0x82F63B78u & (0u - (register & 1)));
    }
}

return ~register;
```

**What the 21 tests check:**

- **Published check values:** "123456789", and the four 32-byte patterns of RFC 3720 (iSCSI),
  appendix B.4.
- **Every length from 0 to 5,000 bytes** against the reference: both block sizes of the 3-way
  kernel several times, and every remainder after them.
- **Large inputs:** 1 MiB, 1 MiB + 1 and 1 MiB + 1,537 bytes.
- **Unaligned data:** 4 KiB starting at each of 16 offsets, since the kernels load unaligned
  quadwords.
- **Continuing a checksum:** splitting the input anywhere, including at the block boundaries,
  gives the same result.
- **The empty input,** from 0 and from a running checksum.
- **The dispatcher:** the path `cpuid` chose matches .NET's view of the CPU, and its results agree
  on both sides of the 4 KiB `SuppressGCTransition` limit.
- **Concurrency:** 64 inputs checksummed by many threads at once.
- **A bad path** is refused before any native code runs.

**The tests were checked against a broken build.** With one merge constant's exponent changed
from 8n − 33 to 8n − 32, 6 of the 21 tests fail: every 3-way test whose input reaches a
192-byte block, and the dispatcher and concurrency tests, which run on the 3-way path. The
correct build passes all 21.

### Running them

An xunit v3 project is an executable, so the tests run wherever the built folder can run:

```sh
# On Windows, Linux or macOS, x64 or ARM64: either one
dotnet test --project samples/Crc32C.Tests/Crc32C.Tests.csproj
dotnet samples/Crc32C.Tests/bin/Release/net10.0/Crc32C.Tests.dll

# The Linux build from Windows: run the same output folder in the .NET runtime container
docker run --rm -v <full path of samples/Crc32C.Tests/bin/Release/net10.0>:/app:ro -w /app mcr.microsoft.com/dotnet/runtime:10.0 dotnet Crc32C.Tests.dll
```

On ARM64 the tests check the `crc32cx` and PMULL paths. On every CPU they run only the paths
`Crc32C.IsSupported` says it has; `Crc32C.Append(…, path)` throws
`PlatformNotSupportedException` for the others, rather than letting an illegal instruction
end the process.

`dotnet test` needs `"test": { "runner": "Microsoft.Testing.Platform" }` in the `global.json`
next to the project, as `samples/global.json` has.

## The benchmark

The benchmark times the library's three paths and its dispatcher against the CRC-32C a C#
developer writes first, `BitOperations.Crc32C` over 8-byte words, at 64 B, 1 KiB, 64 KiB and
1 MiB:

```csharp
[Benchmark(Baseline = true, Description = "C# BitOperations.Crc32C")]
public uint Managed() => ManagedCrc32C(_data);

[Benchmark(Description = "asm crc32c_append (dispatcher)")]
public uint Dispatcher() => Crc32C.HashToUInt32(_data);
```

- **A fair baseline:** `BitOperations.Crc32C` compiles to the same SSE4.2 `crc32` instruction
  as the single-chain path. What the 3-way kernel adds is the three independent chains, not the
  instruction.
- **In process:** the job uses BenchmarkDotNet's in-process toolchain, so it needs no SDK at run
  time and runs in the .NET runtime container.
- **A GB/s column,** from the input size and the mean time.
- **It refuses to time wrong code:** before timing, it checks every path against the managed
  checksum at every size, and `--check` does only that (CI runs it).

```sh
# every benchmark, on any of the eight platforms
dotnet run -c Release --project samples/Crc32C.Bench -- --filter '*'
# only the check
dotnet run -c Release --project samples/Crc32C.Bench -- --check
```

## Results

Three complete runs on an Intel Core i9-13900K, in the .NET runtime container on WSL2 (.NET
10.0.15, BenchmarkDotNet 0.15.8, default job), with other work running on the machine. Each
figure is the median of the three runs; the [results folder](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Bench/results/2026-11-19-i9-13900K) has
the lowest and highest of each, and BenchmarkDotNet's own reports.

"C#" is the `BitOperations.Crc32C` loop; the other four columns are the assembly library:
`crc32c_append` (the dispatcher, which runs the 3-way path here) and each path called directly.

| Time | C# | dispatcher | 3-way | 1 chain | table |
|---|---:|---:|---:|---:|---:|
| 64 B | **3.35 ns** | 13.0 ns | 14.3 ns | 13.7 ns | 101 ns |
| 256 B | 21.2 ns | **18.2 ns** | 18.3 ns | 35.0 ns | 550 ns |
| 1 KiB | 144 ns | **46.3 ns** | 50.1 ns | 85.5 ns | 2.35 μs |
| 64 KiB | 11.0 μs | **2.93 μs** | 2.93 μs | 8.33 μs | 154 μs |
| 1 MiB | 178 μs | 48.5 μs | **48.1 μs** | 132 μs | 2.47 ms |

| GB/s | C# | dispatcher | 3-way | 1 chain | table |
|---|---:|---:|---:|---:|---:|
| 1 KiB | 7.10 | **22.1** | 20.5 | 12.0 | 0.44 |
| 64 KiB | 5.95 | 22.4 | **22.4** | 7.86 | 0.43 |
| 1 MiB | 5.90 | 21.6 | **21.8** | 7.92 | 0.42 |

| One call with an empty input | Median |
|---|---:|
| with the GC transition | 9.60 ns |
| `SuppressGCTransition` | 3.38 ns |

**What they show:**

- **Large inputs: 3.7 times the C# loop.** At 1 MiB the 3-way kernel runs at 21.8 GB/s, the C#
  loop at 5.90 GB/s (3.69x) and the single chain at 7.92 GB/s (the kernel is 2.75x that). Three
  chains do not quite triple the single chain: lane 0 of each block waits for the previous
  block's merge.
- **The single chain beats the C# loop that uses the same instruction, 1.34x.** The JIT
  zero-extends the `uint` checksum before every 64-bit `crc32` (`mov eax, eax`), and that move is
  in the chain of dependent steps: 4 cycles per 8 bytes instead of 3, and 4/3 = 1.33. The
  assembly keeps the checksum in `rax`, whose upper half `crc32` already clears.
  `DOTNET_JitDisasm=ManagedCrc32C` shows the JIT's loop.
- **Small inputs: the call costs as much as the work.** An empty call takes 3.4 ns even without
  the GC transition. At 64 bytes the C# loop, inlined, takes 3.35 ns, and successive checksums
  overlap inside the CPU; the library takes 13 ns. From 256 bytes on, the assembly wins: 1.16x at
  256 B, 3.1x at 1 KiB. A wrapper that sees many tiny inputs could branch to managed code below
  about 200 bytes; this sample always calls the library, since showing that call is its point.
- **`SuppressGCTransition` is worth it below 4 KiB.** It saves 6.2 ns on an empty call; with real
  work part of the transition overlaps the work, and the dispatcher (no transition up to 4 KiB)
  leads the per-path calls (with it) by 0 to 4 ns.
- **The generated binding.** The benchmark also times the binding the SDK
  writes from `crc32c.asm`'s `PROTO` line. It costs what the hand-written import costs: the
  medians were 7.38 ns and 7.36 ns on 2026-11-26, native Windows, pinned to one performance core
  (see [Crc32C, Generated bindings](crc32c.md#generated-bindings); that run's setup differs
  from the table above, so only its ratios compare).
- **The table path, 0.42 GB/s,** is what a CPU without SSE4.2 would get: about 50 times slower
  than the 3-way kernel. That is why the dispatcher exists.
- **Noise:** the hybrid CPU and the virtual machine move the benchmark between core types. The
  64-byte cases varied up to 17% between runs, the 64 KiB and 1 MiB cases within 4%.

## One core and four

**Not released yet: `--cores`.** BenchmarkDotNet times one call on one thread. The benchmark's
`--cores` mode times the C# loop and the library on one core and on several at once:

- **On N cores,** N threads each checksum their own copy of the input, and a cell's figure is
  their total.
- **Each thread is pinned** to a logical CPU of its own (`--cpus`, on Windows and Linux). On
  Windows, the mode also turns power throttling off for its process.
- **Paired rounds** alternate which side goes first.
- **Checks:** every thread compares its checksum with the managed one before and after its
  timed window.

```sh
# one core and four, on performance cores 1 to 4 of an i9-13900K
dotnet run -c Release --project samples/Crc32C.Bench -- --cores 1,4 --cpus 2,4,6,8
```

Three runs on 2026-11-26, natively on Windows. The cores ran at 2.9 GHz, and `coreguard` held
each timed core's other hyperthread. The
[results folder](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Bench/results/2026-11-26-i9-13900K-cores)
has the setup, every round's raw counts and the scripts. Below are the medians of 36 rounds,
all cores together:

| Size | C#, 1 core | Assembly, 1 core | C#, 4 cores | Assembly, 4 cores | Assembly / C# |
|---|---:|---:|---:|---:|---:|
| 64 B | 187M/s | 115M/s | 749M/s | 461M/s | 0.61x |
| 256 B | 9.31 GB/s | 19.1 GB/s | 37.2 GB/s | 76.4 GB/s | 2.04x |
| 1 KiB | 6.78 GB/s | 19.6 GB/s | 27.2 GB/s | 78.3 GB/s | 2.88x |
| 64 KiB | 5.76 GB/s | 20.1 GB/s | 23.1 GB/s | 80.4 GB/s | 3.49x |
| 1 MiB | 5.74 GB/s | 20.0 GB/s | 23.0 GB/s | 79.9 GB/s | 3.49x |

- **Four cores do four times the work, on both sides:** 4.00x at every size, since each
  thread's input stays in its own core's caches. So the library's lead is the same on four
  cores as on one, and so is the C# loop's at 64 B.
- **The run's BenchmarkDotNet cross-check found a trap in the first table.** Run natively on the
  same core, BenchmarkDotNet timed the dispatcher at 64 B at 24.2 ns with tiered compilation on,
  and at 9.92 ns with it off.
  - **The cause:** the JIT's log shows the C# methods between BenchmarkDotNet and the library
    (`Dispatcher()`, `HashToUInt32`, `Append`) still running unoptimized Tier-0 code through the
    small sizes. They reached Tier-1 only during the 64 KiB case.
  - **So the first table's library figures at 64 B and 256 B** (13.0 and 18.2 ns) most likely
    include that unoptimized glue too.
  - **With optimized code,** the same pinned core gives 8.72 ns at 64 B and 13.4 ns at 256 B.
    The C# loop still wins at 64 B (1.6x), and the library wins from 256 B (2.0x).
