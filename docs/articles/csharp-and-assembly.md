# C# and assembly, the easy way

*Write the hot loop in NASM, keep everything else in C#, and let `dotnet build`, NuGet and your
IDE treat both the same.*

Most C# never needs assembly. The JIT is good, `System.Runtime.Intrinsics` exposes SIMD, and
`Span<T>` removed most of the reasons to drop to native code. But now and then a routine sits on
the hot path of everything: a checksum, a parser, a hash. Then you want control over every
instruction, which registers hold what, and which instructions wait for which.

Until recently, that meant leaving .NET's world. You installed an assembler and a linker on every
machine, wrote a Makefile or a CMake script, worked out the linker flags for a shared library,
copied the `.so` next to your program by hand, and repackaged it for NuGet in a layout you looked
up each time. Your IDE showed none of it.

AsmSdk removes all of that. An assembly project is a project file, `.asmproj`, built by
`dotnet build` like a `.csproj`. A C# project references it with a `ProjectReference`. Both
ship as NuGet packages. Rider and Visual Studio show its files. The assembler and linker come
from NuGet too, so a fresh machine needs only the .NET SDK.

This article walks through a real example: CRC-32C, the checksum of iSCSI, ext4 and gRPC,
written in assembly, called from C#, tested with xunit, measured with BenchmarkDotNet and shipped
through NuGet.

## Three files

A shared library in assembly takes three files. `global.json` names the SDK, as it would for any
MSBuild SDK:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.2.0" } }
```

The project file:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>crc32c</AssemblyName>
  </PropertyGroup>
</Project>
```

And the code, in NASM's Intel syntax, in a `.asm` file next to the project. `dotnet build` turns
it into `bin/Debug/freestanding/libcrc32c.so`. "Freestanding" is the target framework: the library
needs no C library, so it loads into any Linux x64 process. Its sibling, `libc`, builds code that
calls glibc.

Calling it from C# is the part that used to hurt, and now isn't:

```xml
<ItemGroup>
  <ProjectReference Include="../Crc32C/Crc32C.asmproj" />
</ItemGroup>
```

```csharp
[LibraryImport("crc32c", EntryPoint = "crc32c_append")]
private static partial uint Append(uint crc, byte* data, nuint length);
```

The reference builds the assembly project first and copies `libcrc32c.so` next to your
assembly. `[LibraryImport]` generates the call at compile time. That's all the plumbing there is.

## Why write this one in assembly?

The CRC-32C a C# developer writes first is short and already uses the hardware: SSE4.2 has an
instruction for exactly this checksum, and .NET exposes it as `BitOperations.Crc32C`.

```csharp
uint crc = ~0u;
while (data.Length >= 8)
{
    ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data);
    crc = BitOperations.Crc32C(crc, word);
    data = data[8..];
}
```

The catch is that each step needs the previous step's result. The `crc32` instruction takes 3
cycles to produce a result but could start a new one every cycle, so this loop leaves it idle two
cycles out of three. The fix is old, and well known from Intel's papers and the Linux kernel: cut
each block into three lanes, run three independent chains, then merge the three partial
checksums with a carry-less multiply (PCLMULQDQ).

```nasm
.lanes:
    crc32   rax, qword [rsi]            ; lane 0
    crc32   r8,  qword [rsi + 512]      ; lane 1
    crc32   r9,  qword [rsi + 1024]     ; lane 2
    add     rsi, 8
    dec     ecx
    jnz     .lanes
```

You could write this in C# with intrinsics. In assembly, the structure that matters is the code
itself: three registers, three chains, no compiler deciding otherwise. The merge needs constants,
x^(8n−33) mod P for each lane size, and NASM's preprocessor computes them while it assembles the
file, so none are copied from anywhere:

```nasm
%macro X_POW 1                          ; CRC32C_XPOW = x^n mod P, bit-reflected
    %assign CRC32C_XPOW 0x80000000
    %rep %1
        %assign CRC32C_XPOW (CRC32C_XPOW >> 1) ^ (CRC32C_POLY & -(CRC32C_XPOW & 1))
    %endrep
%endmacro
```

The library also keeps a table-driven path for CPUs without SSE4.2 and a single-chain path for
CPUs without PCLMULQDQ. Its first call asks the CPU, with `cpuid`, which of the three it can run,
and every later call jumps straight to that one.

## The numbers

Three complete benchmark runs with BenchmarkDotNet, on an Intel Core i9-13900K, in the .NET
runtime container on WSL2 (medians; the raw reports are in the repository):

```text
              C# BitOperations    asm, one chain    asm, 3-way (crc32c_append)
1 MiB         5.90 GB/s           7.92 GB/s         21.6 GB/s
64 KiB        5.95 GB/s           7.86 GB/s         22.4 GB/s
1 KiB         144 ns              85.5 ns           46.3 ns
256 bytes     21.2 ns             35.0 ns           18.2 ns
64 bytes      3.35 ns             13.7 ns           13.0 ns
```

On large inputs the library is 3.7 times the C# loop. Two things in that table deserve a closer
look:

- **Even the single chain beats C#, by 1.34x, with the same instruction.** The JIT zero-extends
  the `uint` before every 64-bit `crc32` (a `mov eax, eax`), and that move sits in the chain of
  dependent steps: 4 cycles per 8 bytes instead of 3. 4/3 = 1.33. `DOTNET_JitDisasm` shows it in
  one line.
- **At 64 bytes, C# wins by a mile.** A call into native code costs 3.4 ns even with
  `[SuppressGCTransition]` (9.6 ns without), as much as the whole checksum. From 256 bytes on,
  the assembly is ahead. So the rule is the usual one: put assembly behind calls that do enough
  work, and measure where "enough" starts.

## Test it like any other .NET code

The assembly is tested in C#, with xunit, against a reference written the slowest and most
obvious way: one bit at a time, no table and no special instruction, so the two can't share a
mistake.

```csharp
[Theory]
[MemberData(nameof(Paths))]                  // each path this CPU can run
public void Every_length_matches_the_reference(Crc32CPath path)
{
    byte[] data = RandomBytes(5000, seed: 1);
    for (int length = 0; length <= data.Length; length++)
    {
        ReadOnlySpan<byte> slice = data.AsSpan(0, length);
        Assert.Equal(Reference.Append(0, slice), Crc32C.Append(0, slice, path));
    }
}
```

Every length from 0 to 5,000 bytes crosses both block sizes of the 3-way kernel several times,
and every remainder after them. Other tests cover a megabyte, unaligned data, continuing a
checksum, and 64 threads at once. To check that the tests can actually fail, the library was
built once with a wrong exponent in one merge constant: 6 of the 21 tests failed. The library is a
Linux ELF file, so the tests run on Linux. On Windows, the built test project runs as-is in the
.NET runtime container.

## Ship it through NuGet

`dotnet pack` on the assembly project produces a package with the library under
`runtimes/linux-x64/native/`, where .NET looks for native files. Packing the C# wrapper turns its
`ProjectReference` into a package dependency. A user writes one line:

```xml
<PackageReference Include="Samples.Crc32C.Net" Version="1.0.0" />
```

They get the wrapper, NuGet brings the native package along, and the build copies
`libcrc32c.so` into their output. Nothing in their project mentions assembly.

## What you give up

- **Linux x86-64 only.** AsmSdk builds Linux ELF files. You can build on Windows, Linux or macOS,
  but the outputs run on Linux: on Windows, use WSL or a container.
- **You own the details** a compiler handled: the calling convention, stack alignment,
  callee-saved registers, which CPU features you may use. AsmSdk's documentation lists them, and
  its examples show each one, but nothing checks them for you except your tests.
- **Most code should stay C#.** Assembly pays for a small routine that runs a lot. Measure
  first, and keep the slow C# version as the reference your tests compare against.

## Try it

- **Documentation:** [asmfoundation.github.io/AsmSdk](https://asmfoundation.github.io/AsmSdk/),
  with [Getting started](https://asmfoundation.github.io/AsmSdk/guide/getting-started.html) and
  the [Examples](https://asmfoundation.github.io/AsmSdk/examples/index.html), including this CRC-32C.
- **Source:** [github.com/AsmFoundation/AsmSdk](https://github.com/AsmFoundation/AsmSdk), the
  example under `samples/Crc32C*`.
- **Package:** [AsmFoundation.Sdk on nuget.org](https://www.nuget.org/packages/AsmFoundation.Sdk).

AsmSdk is open source under the Apache-2.0 license, part of the
[AsmFoundation](https://github.com/AsmFoundation).
