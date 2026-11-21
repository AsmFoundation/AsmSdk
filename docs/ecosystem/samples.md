# Samples

The [`samples/`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples) folder of the
AsmSdk repository covers every kind of project. CI builds and checks all of them on five
build machines: Windows x64 and Windows on Arm, Linux x64 and ARM64, and Apple silicon. Crc32C
is built for all seven RIDs. The `Hello.*` samples, WordCount and HexDump are Linux x64 code
(Linux system calls, or glibc), and say so with `<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>`.

**The small `Hello.*` samples** show one kind of project each, in a few lines:

| Sample | Shows |
|---|---|
| [`Hello.Library`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Library) | a shared library (`libhello.so`) with a public `include/` file, built for both target frameworks (`TargetFrameworks=freestanding;libc`): one source, `%ifdef LIBC` |
| [`Hello.Static`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Static) | a static library (`libhellostatic.a`) with public includes, packed as the NuGet package `Hello.Static` 1.0.0 |
| [`Hello.App`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.App) | a freestanding executable that links `Hello.Static` through a `ProjectReference` |
| [`Hello.Libc`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Libc) | a `main()` program on glibc that calls `printf` |
| [`Hello.LibcLibrary`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.LibcLibrary) | a shared library that calls `strlen` through its PLT |
| [`consumers/Hello.Consumer`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/consumers/Hello.Consumer) | an executable that consumes the `Hello.Static` package by version |
| [`Hello.Net`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Net) | a C# program that P/Invokes both shared libraries, and checks that it was given the `libc` build of `Hello.Library` |

**The realistic samples** are complete programs, each walked through in the
[Examples](../examples/index.md):

| Sample | Shows |
|---|---|
| [`Crc32C`](../examples/crc32c.md) | CRC-32C as a freestanding shared library for seven RIDs: x64 in NASM through `asmsdk.inc`, ARM64 in GNU syntax, `ExportsFile`, run-time CPU dispatch on each OS, constants computed by the assemblers, a 3-way kernel (SSE4.2 + PCLMULQDQ, or CRC32 + PMULL) |
| [`Crc32C.Net`](../examples/crc32c.md#calling-it-from-c), [`Crc32C.Tests`](../examples/testing-and-benchmarks.md), [`Crc32C.Bench`](../examples/testing-and-benchmarks.md) | its C# API (`[LibraryImport]`, `SuppressGCTransition`), its xunit tests, its BenchmarkDotNet benchmark |
| [`consumers/Crc32C.App`](../examples/nuget-for-dotnet.md) | a .NET program that uses the library through two NuGet packages, by version |
| [`WordCount`](../examples/wordcount.md) | `wc` on glibc: PLT calls, a libc data object through the GOT, `AsmLibcSymbol`, SSE2 counting |
| [`HexDump`](../examples/hexdump.md) | `xxd` with no libc: `_start`, raw system calls, a static executable of about 4 KB |

`Samples.slnx` holds them all except the two under `consumers/`, which sit outside the solution
because the packages they use must be packed first.

## How they are checked

[`tests/AsmSdk.Tests`](https://github.com/AsmFoundation/AsmSdk/tree/main/tests/AsmSdk.Tests),
an xunit v3 project, packs the SDK and the toolchain into a local feed, then builds the
samples against it, with a fresh `NUGET_PACKAGES`. Each of its 119 tests checks one
expectation:

- **the seven RIDs** (`CrossPlatformTests`): one project's object format, exports and
  qualified sources per RID; the C library of each OS; programs that export what their
  `ExportsFile` lists; the .NET consumer, portable and per RID; the package layout; the RID
  errors; the image checks (`ASM0080`, `ASM0081`). Each machine also runs its own platform's
  build, and the x64 one under emulation on Windows on Arm and Apple silicon;
- **the ELF properties of every Linux output:** type, SONAME, `DT_NEEDED`, interpreter,
  exports and archive members;
- **that the executables run,** and that the C# program's calls into both libraries return
  the right values;
- **incremental builds:** a rebuild without changes runs no tool, and an include-file
  change reassembles what depends on it;
- **the package layouts** of a single-target and a multi-target library;
- **the projects in [`tests/projects`](https://github.com/AsmFoundation/AsmSdk/tree/main/tests/projects),**
  which cover what the samples cannot: the target framework compatibility errors (`NU1201`,
  `NU1202`), the errors for settings removed in 0.2.0, `StartupObject`, `ExportsFile`,
  `LinkerArg`, `DefineConstants` and `DebugSymbols`;
- **IDE integration:** the declared item types, that every file of a project folder is one
  item, the up-to-date inputs and outputs, and that a design-time build runs no tool (see
  [IDEs](../guide/ides.md));
- **the realistic samples** (`ExampleTests`): Crc32C's exports, constants, packages, own unit
  tests and NuGet consumer; WordCount's imports, counts and errors; HexDump's size, output and
  errors (see [Examples](../examples/index.md#how-they-are-checked)).

The .NET programs (Crc32C's tests, its benchmark's check, the NuGet consumer) run natively on
every machine: their portable output carries every RID's library. The Linux x64 programs run
natively on Linux x64, and elsewhere through WSL or Docker when available. Where neither is,
those tests are skipped with the reason (`ASMSDK_TEST_RUN` and `ASMSDK_TEST_DOTNET_RUN`
choose explicitly). Only the .NET SDK is needed:

```sh
dotnet run toolchain/fetch.cs -- --rid win-x64                 # or win-arm64, linux-x64, linux-arm64, osx-arm64: stages the tools
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj   # packs, builds the samples, checks and runs them
```

## Bytes on the home page

The block A on the home page is labelled with machine code from these functions:

- **the mint blocks** are `hello_add` from `Hello.Library`: `lea eax, [rdi + rsi]` assembles
  to `8D 04 37`, and `ret` to `C3`;
- **the teal blocks** are the first bytes of `sum_i32`, the freestanding template in
  [Writing NASM for AsmSdk](../guide/writing-nasm.md#templates), as assembled for Linux and
  macOS: `xor eax, eax` (`31 C0`), `test rsi, rsi` (`48 85 F6`), `jle` (`7E 0F`), then
  `movsxd r8, …` (`4C 63 …`).
