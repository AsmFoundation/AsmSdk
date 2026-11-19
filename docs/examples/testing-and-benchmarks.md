# Testing and measuring from C#

An assembly library is tested and measured like any .NET code: with an xunit project and a
BenchmarkDotNet project that call it through its C# API. This page shows both for
[Crc32C](crc32c.md): [`samples/Crc32C.Tests`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Tests)
and [`samples/Crc32C.Bench`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Bench).

## The tests

The test project references the C# wrapper, which references the `.asmproj`, so building the
tests builds the assembly too and puts `libcrc32c.so` next to the test assembly.

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
# Linux x64: either one
dotnet test --project samples/Crc32C.Tests/Crc32C.Tests.csproj
dotnet samples/Crc32C.Tests/bin/Release/net10.0/Crc32C.Tests.dll

# Windows: build there, then run the output folder in the .NET runtime container
dotnet build samples/Crc32C.Tests/Crc32C.Tests.csproj -c Release
docker run --rm -v <full path of samples/Crc32C.Tests/bin/Release/net10.0>:/app:ro -w /app mcr.microsoft.com/dotnet/runtime:10.0 dotnet Crc32C.Tests.dll
```

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
# Linux x64: every benchmark
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
- **The table path, 0.42 GB/s,** is what a CPU without SSE4.2 would get: about 50 times slower
  than the 3-way kernel. That is why the dispatcher exists.
- **Noise:** the hybrid CPU and the virtual machine move the benchmark between core types. The
  64-byte cases varied up to 17% between runs, the 64 KiB and 1 MiB cases within 4%.
