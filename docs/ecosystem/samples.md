# Samples

The [`samples/`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples) folder of the
AsmSdk repository covers every kind of project. CI builds and checks all of them on five
build machines: Windows x64 and Windows on Arm, Linux x64 and ARM64, and Apple silicon. Crc32C
is built for all eight native RIDs and the two WebAssembly ones. The `Hello.*` samples and
WordCount are Linux x64 code (Linux system calls, or glibc), and say so with
`<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>`; HexDump is Linux x64 code and a WASI
command (`linux-x64;wasi-wasm`).

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
| [`Crc32C`](../examples/crc32c.md) | CRC-32C as a freestanding shared library for the eight native RIDs: x64 in NASM through `asmsdk.inc`, ARM64 in GNU syntax, `ExportsFile`, `PROTO` lines, run-time CPU dispatch on each OS, constants computed by the assemblers, a 3-way kernel (SSE4.2 + PCLMULQDQ, or CRC32 + PMULL); and `browser-wasm` and `wasi-wasm` (`crc32c.wasm.s`, slicing-by-8) |
| [`Crc32C.Net`](../examples/crc32c.md#calling-it-from-c), [`Crc32C.Tests`](../examples/testing-and-benchmarks.md), [`Crc32C.Bench`](../examples/testing-and-benchmarks.md) | its C# API (`[LibraryImport]`, `SuppressGCTransition`), its xunit tests, its BenchmarkDotNet benchmark (which also times the binding the SDK writes from the `PROTO` lines) |
| [`consumers/Crc32C.App`](../examples/nuget-for-dotnet.md) | a .NET program that uses the library through two NuGet packages, by version |
| [`WordCount`](../examples/wordcount.md) | `wc` on glibc: PLT calls, a libc data object through the GOT, `AsmLibcSymbol`, SSE2 counting |
| [`HexDump`](../examples/hexdump.md) | `xxd` with no libc: `_start`, raw system calls, a static executable of about 4 KB; and `wasi-wasm` (`main.wasi.s`, a WASI command of about 2.5 KB) |

`Samples.slnx` holds them all except the two under `consumers/`, which sit outside the solution
because the packages they use must be packed first.

**The [WebAssembly](../guide/webassembly.md) samples** run in a browser, Node or wasmtime:

| Sample | Shows |
|---|---|
| [`Crc32C.Browser`](../examples/crc32c.md#in-webassembly) | the Crc32C library in a .NET WebAssembly app, through the unchanged `Crc32C.Net`: checks it against .NET's own CRC-32C, times both in Node and Chrome with the C# interpreted and AOT-compiled, and has a page that checksums a dropped file |
| [`WasmDemos`](../demos/index.md) (`Life`, `Ray`, `Swarm`) | the [Wasm Demos](../demos/index.md) this site runs: Conway's Life on 16 million cells (bit-sliced, 128 cells per instruction), a ray-marched scene (four rays per `f32x4`, lane masks, progressive passes) and a swarm of 120,000 particles, each a `browser-wasm` module that simulates and draws its own frame; `check.mjs` holds each against a reference |

`Crc32C.Browser` needs the `wasm-tools` workload, so it is outside `Samples.slnx` too. The three
`WasmDemos` kernels need nothing but the SDK, and are in it.

## How they are checked

[`tests/AsmSdk.Tests`](https://github.com/AsmFoundation/AsmSdk/tree/main/tests/AsmSdk.Tests),
an xunit v3 project, packs the SDK and the toolchain into a local feed, then builds the
samples against it, with a fresh `NUGET_PACKAGES`. Each of its tests (482 at 0.4.0) checks one
expectation:

- **the eight native RIDs** (`CrossPlatformTests`): one project's object format, exports and
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
- **IDE integration:** the item types, that every file of a project folder is one item, the
  up-to-date inputs and outputs, that a design-time build runs no tool, and what Rider's and
  Visual Studio's design-time builds hand their C# engines of the generated bindings (see
  [IDEs](../guide/ides.md));
- **the generated bindings and the C header** (`ApiTests`, `BindingsTests`, `StructTests`,
  `PackageBindingsTests`, `CHeaderTests`): the API file on every RID, every C type and struct
  field kind called from C#, the asm code reading back what C# wrote, packages with their
  bindings and no SDK dependency, and a C program compiled against the header (where a C
  compiler is);
- **the realistic samples** (`ExampleTests`): Crc32C's exports, constants, packages, own unit
  tests and NuGet consumer; WordCount's imports, counts and errors; HexDump's size, output and
  errors (see [Examples](../examples/index.md#how-they-are-checked));
- **whole small repositories** (`ScenarioTests`): each folder of
  `tests/scenarios` is a solution taken through the `dotnet` commands its `scenario.json`
  lists. Asm libraries in C# apps, in unit tests, beside NumSharp, OptunaSharp and
  KvAsm.Client; asm programs that link libraries, that a C# app starts, and that are packed as
  .NET tools (see [Scenario tests](scenarios.md)).

The .NET programs (Crc32C's tests, its benchmark's check, the NuGet consumer) run natively on
every machine: their portable output carries every RID's library. The Linux x64 programs run
natively on Linux x64, and elsewhere through WSL or Docker when available. Where neither is,
those tests are skipped with the reason (`ASMSDK_TEST_RUN` and `ASMSDK_TEST_DOTNET_RUN`
choose explicitly). `WebAssemblyTests` and the WebAssembly cases of `ExampleTests` run the modules in Node and the WASI programs in wasmtime wherever those are on
`PATH`, and link the .NET WebAssembly apps where the workloads are; `ASMSDK_TEST_WASM_APPS`
chooses for the apps: `auto` (run, or skip with the reason), `required` (fail instead of
skipping), `none`, or the RIDs whose app tests must run (`browser-wasm`, as on CI's Windows x64).
Only the .NET SDK is needed:

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
