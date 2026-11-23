# AsmSdk

[![CI](https://github.com/AsmFoundation/AsmSdk/actions/workflows/ci.yml/badge.svg)](https://github.com/AsmFoundation/AsmSdk/actions/workflows/ci.yml)
[![NuGet: AsmFoundation.Sdk](https://img.shields.io/nuget/v/AsmFoundation.Sdk.svg?label=AsmFoundation.Sdk)](https://www.nuget.org/packages/AsmFoundation.Sdk)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-asmfoundation.github.io%2FAsmSdk-06706d.svg)](https://asmfoundation.github.io/AsmSdk/)

**Enterprise-grade project, dependency and build management for assembly, on MSBuild and
NuGet.** This repo holds the MSBuild SDK behind `.asmproj`, the assembly project type that
sits next to `.csproj`.

- It builds assembly into shared libraries, static libraries and programs for **Windows,
  Linux and macOS, on x64 and ARM64**, with `dotnet build`: seven runtime identifiers, from
  any build machine.
- A C# project that references an `.asmproj` gets the binary of the platform it is built or
  run for, chosen by .NET's own runtime identifier rules.
- It shares asm libraries as versioned NuGet packages.
- Nothing needs to be installed: restore downloads a bundled toolchain (NASM 3.02, plus LLVM
  23.1.5's `llvm-mc`, `lld` and `llvm-ar`) for the build machine, and the SDK writes the C
  library stand-ins itself, so no sysroot or platform SDK is needed.
- The project file reads like a `.csproj`: `RuntimeIdentifiers`, `TargetFramework`,
  `OutputType`, `AssemblyName`, `DefineConstants`, `DebugSymbols` and `StartupObject` are
  .NET's own properties, with their .NET meaning.

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>hello</AssemblyName>
    <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-arm64</RuntimeIdentifiers>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Hello.Static" Version="1.0.0" />   <!-- an asm package -->
  </ItemGroup>
</Project>
```

Part of the [AsmFoundation](https://github.com/AsmFoundation). AsmSdk is a
product of its own, not an internal tool of another project. It is for any
assembly codebase that wants the project, dependency and build management
the .NET world takes for granted:

- solutions;
- versioned packages with transitive restore;
- reproducible toolchains;
- CI on every OS.

## Why

Assembly projects usually live outside the .NET build: a Makefile per platform, a Docker
toolchain image, and libraries copied around by hand. AsmSdk brings the parts the .NET world
takes for granted to them:

- **One build command** for the whole solution, asm and C# together, every platform at once.
- **Real dependency management:** an asm library is a NuGet package with a
  version, so other asm projects consume it with `PackageReference`. Transitive
  dependencies flow, and restore fetches them.
- **No install step:** the toolchain is a NuGet package too, pinned by version
  and verified by SHA-256 when it is built.
- **Incremental builds** that are exact: a rebuild without changes starts no
  tool, and editing an include file reassembles only the sources that can
  include it.

## Quick start

1. `global.json` next to your solution:

   ```json
   { "msbuild-sdks": { "AsmFoundation.Sdk": "0.3.0" } }
   ```

2. `Hello/Hello.asmproj` (as above), with `.asm` files (NASM, the x64 code) and `.s` files
   (GNU syntax, the ARM64 code) next to it. Every `*.asm` and `*.s` is a source and every
   `*.inc` an include file, the way every `*.cs` is compiled in a `.csproj`. A qualifier
   narrows a file to some platforms: `hello.linux.asm`, `hello.win-arm64.s`.
3. `dotnet build`:

   ```text
   Hello -> bin/Debug/freestanding/win-x64/hello.dll
   Hello -> bin/Debug/freestanding/linux-x64/libhello.so
   Hello -> bin/Debug/freestanding/osx-arm64/libhello.dylib
   …
   ```

In a `.slnx` solution, give the project the generic MSBuild project type:

```xml
<Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />
```

A project can name the SDK version itself instead of `global.json`:
`<Project Sdk="AsmFoundation.Sdk/0.3.0">`.

The [getting started guide](https://asmfoundation.github.io/AsmSdk/guide/getting-started.html)
builds a library, a program and a C# caller for seven platforms.

Not released yet: on `main`, plug and play. `dotnet new install AsmFoundation.Templates`, then
`dotnet new asmnet -n Demo`, creates a C# app, the asm library it calls and xunit tests, which
`dotnet build`, `run` and `test` take as they are (`asmlib`, `asmexe` and `asmstatic` create one
project each). A project that lists no `RuntimeIdentifiers` builds for the build machine's
platform, with `ASM0013` when a pack or a portable publish would ship that one alone.
`ImplicitUsings` puts `asmsdk.inc`, `default rel` and the Linux `.note.GNU-stack` in front of
every source, and `dotnet sln add` writes the project type itself. `NoWarn`,
`WarningsAsErrors` and `WarningsNotAsErrors` take NASM's warning names, `NativeLibrary`
replaces `AsmLinkLibrary`, and Release builds are byte-identical in any folder.

## What it builds

| RID | Shared library | Static library | Program | C library (`libc`) |
|---|---|---|---|---|
| `win-x64`, `win-arm64` | `<name>.dll` + `<name>.dll.lib` | `<name>.lib` | `<name>.exe` | the Universal C Runtime |
| `linux-x64`, `linux-arm64` | `lib<name>.so` | `lib<name>.a` | `<name>` | glibc 2.34+ |
| `linux-musl-x64` | `lib<name>.so` | `lib<name>.a` | `<name>` | musl (Alpine) |
| `osx-x64`, `osx-arm64` | `lib<name>.dylib` | `lib<name>.a` | `<name>` (`libc` only) | libSystem |

Not released yet: `main` also builds `linux-musl-arm64` (musl on ARM64, such as Alpine on
Graviton), and **WebAssembly**: `browser-wasm` and `wasi-wasm`, from LLVM's WebAssembly
assembly in `.s` files qualified for it (`name.wasm.s`). A library is a module, `<name>.wasm`,
plus the archive `<name>.a`, which a .NET WebAssembly app (Blazor, WASI) links into its
runtime. A `wasi-wasm` program is a WASI command that `dotnet run` starts in wasmtime (see
[WebAssembly](docs/guide/webassembly.md)). AsmFoundation.Sdk 0.3.0 on nuget.org builds the
seven RIDs above; see the [changelog](CHANGELOG.md).

`OutputType` picks the kind: `Library` (the default), `StaticLibrary`, `Exe`, or `Objects`
for object files another project links. Outputs go to `bin/<Configuration>/<tfm>/<rid>/`.

Not released yet: on `main`, a program (`Exe`) gets the asm shared libraries it links next to
it, and the run path to find them there. `dotnet run` starts it on this machine, through WSL
or in Docker. `dotnet publish` writes one folder per RID. A .NET app or another program can
reference it, or its package, and gets it with its libraries. `<PackAsTool>true</PackAsTool>`
packs it as a .NET tool for `dotnet tool install`. See
[Programs](https://asmfoundation.github.io/AsmSdk/guide/programs.html) and the
[changelog](CHANGELOG.md).

- **The platform layer:** `asmsdk.inc` (NASM) names each OS's argument registers (`ARG1`..),
  shadow space and PLT, and its `FUNCTION` macro exports a function the way each object format
  does; `asmsdk-gnu.inc` does the same for ARM64. The SDK defines `WINDOWS`, `LINUX`, `MACOS`,
  `MUSL`, `X64` and `ARM64`.
- **The target framework** says what the code links against, the way `net10.0` says which .NET
  a program runs on:
  - **`freestanding`** (the default): no C library. Linux system calls, kernel32 on Windows.
  - **`libc`:** the platform's C library, through stand-ins the SDK writes from its symbol
    lists: a stub `libc.so.6` (or musl's), `libSystem.tbd`, the UCRT import library.
  - **`<TargetFrameworks>freestanding;libc</TargetFrameworks>`** builds both, like a
    multi-targeted `.csproj`. A `libc` project may reference `freestanding` code; the reverse
    fails at restore (`NU1201`, `NU1202`).
- **The image checks:** after each Windows and macOS link, the SDK rejects load-time fixups
  that those linkers accept but the loaders cannot apply (`ASM0080`, `ASM0081`).

Every property, item and error is described in [docs/asmproj.md](docs/asmproj.md), and
the guides are on the [documentation site](https://asmfoundation.github.io/AsmSdk/). AI
tools can read the whole site as Markdown: start at
[llms.txt](https://asmfoundation.github.io/AsmSdk/llms.txt), or take
[llms-full.txt](https://asmfoundation.github.io/AsmSdk/llms-full.txt) in one piece.

## Sharing code

| From → to | Mechanism | Effect |
|---|---|---|
| `.asmproj` → `.asmproj` | `ProjectReference` | adds the other project's `include/` folder and links its library, built for the same RID |
| `.csproj` → `.asmproj` | `ProjectReference` | a portable build gets every RID's library in `runtimes/<rid>/native/`, listed in `deps.json`; a RID-specific build gets the best match beside the program. No assembly reference |
| package → `.asmproj` | `PackageReference` | the package's generated `build/<id>.props` adds its `include/` and the library of the project's RID |
| package → `.csproj` | `PackageReference` | NuGet copies the `runtimes/<rid>/native/` libraries, as for any native package |

`dotnet pack` on an `.asmproj` builds every RID and writes one package:

| Path | Contents |
|---|---|
| `include/` | public include files |
| `src/` | the sources, with `AsmPackSources=true` |
| `lib/<framework>/_._` | an empty marker per target framework, so NuGet knows which projects may use the package |
| `native/<rid>/<tfm>/` | the static library, and a Windows DLL's import library |
| `runtimes/<rid>/native/` | the shared library (`nativeassets/<framework>/` when the project targets both frameworks) |
| `build/<framework>/` and `buildTransitive/<framework>/` | the generated props, per target framework |

`<tfm>` is `freestanding` or `libc`, and `<framework>` the NuGet framework behind it
(`netstandard1.0` or `netstandard2.0`).

## Samples

[`samples/`](samples) covers every case, and [`tests/AsmSdk.Tests`](tests/AsmSdk.Tests)
builds and checks it on every CI run:

| Sample | Shows |
|---|---|
| `Crc32C` | CRC-32C as a freestanding shared library for seven RIDs: one x64 source for every OS through `asmsdk.inc`, an ARM64 source, exports file, run-time CPU dispatch, a 3-way kernel (SSE4.2 + PCLMULQDQ, CRC32 + PMULL) |
| `Crc32C.Net`, `Crc32C.Tests`, `Crc32C.Bench` | its C# API, xunit tests and BenchmarkDotNet benchmark, which run on every platform |
| `consumers/Crc32C.App` | a .NET program using the library through two NuGet packages, by version |
| `WordCount` | `wc` on glibc: libc calls through the PLT, `AsmLibcSymbol`, SSE2 counting |
| `HexDump` | `xxd` with no libc: raw system calls, a static executable of about 4 KB |
| `Hello.Library` | shared library built for both target frameworks (`TargetFrameworks=freestanding;libc`), with `%ifdef LIBC` |
| `Hello.Static` | static library with public includes, packed as a NuGet package |
| `Hello.App` | freestanding executable linking `Hello.Static` through a ProjectReference |
| `Hello.Libc` | `main()` program calling `printf` on glibc |
| `Hello.LibcLibrary` | shared library calling `strlen` through its PLT |
| `consumers/Hello.Consumer` | consumes the `Hello.Static` package by version |
| `Hello.Net` | C# program P/Invoking both shared libraries |

The first five are walked through in the documentation's
[Examples](https://asmfoundation.github.io/AsmSdk/examples/index.html). WordCount, HexDump
and the `Hello.*` samples are Linux x64 code (system calls, glibc).

## How it works

- **The base:** `AsmFoundation.Sdk` rides on `Microsoft.NET.Sdk`, as Microsoft's
  NoTargets SDK does. Restore, references, versioning, pack and solution builds
  are the standard ones.
- **What it replaces:** compilation (`CoreCompile` becomes assemble + link, once per RID),
  and the few managed steps that make no sense for native code (no assembly, no apphost, no
  assembly reference added to a referencing `.csproj`).
- **.NET consumers:** every `.asmproj` carries an implicit `PackageReference` to the SDK's own
  package, whose `buildTransitive` targets reach referencing .NET projects and wire the native
  libraries in by RID. Packing removes that dependency again.
- **Restore:** a `PackageDownload` fetches `AsmFoundation.Toolchain.<host-rid>`.
- **The toolchain packages** are built by
  [`toolchain/fetch.cs`](toolchain/fetch.cs) from pins in
  [`toolchain/versions.json`](toolchain/versions.json), for five build machines (`win-x64`,
  `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`):
  - the official NASM binary on Windows (under x64 emulation on Windows on Arm);
  - NASM built from the official source on Linux (statically) and on macOS;
  - the official LLVM release's `llvm-mc`, `lld`, `llvm-ar` and `llvm-readobj`.

## Limits

- **x64 code is NASM, ARM64 code GNU syntax.** One x64 file serves every OS through
  `asmsdk.inc`; ARM64 shares one calling convention across the three OSes.
- **libc programs need glibc 2.34+ on glibc Linux.** That is when the `pthread_*` and `dl*`
  functions moved into `libc.so.6`.
- **macOS has no freestanding programs** (Apple supports no static executables): a macOS
  program is `libc` (`ASM0015`). Freestanding libraries are fine.
- **Build machines without a toolchain package** (Intel Macs, Alpine) use
  `AsmToolchain=System` with their own `nasm`, `llvm` and `lld` (`ASM0006`).
- **Linking against a static glibc** needs a real sysroot. Use
  `AsmToolchain=System` on Linux for that.
- **Each machine runs its own platform's outputs,** plus the x64 ones under emulation on
  Windows on Arm and Apple silicon. Linux outputs built elsewhere run in WSL or a container.

## IDEs

Rider, Visual Studio and VS Code show an `.asmproj` like a `.csproj`: its `.asm` and `.s`
sources (`AsmSource`; not released yet: `Compile`), `.inc` files (`AsmInclude`; not released yet: `None`) and every other file of the
folder (`None`). Opening a solution never assembles or links: design-time builds
skip the tools. See [IDEs](https://asmfoundation.github.io/AsmSdk/guide/ides.html).

## Development

Only the .NET SDK (10 or later) is needed:

```sh
dotnet run toolchain/fetch.cs -- --rid win-x64                 # or win-arm64, linux-x64, linux-arm64, osx-arm64: stages the tools
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj   # packs, builds the samples, checks and runs them
dotnet run tests/qa/delivery-matrix.cs                         # before a release: every delivery path, RID and layout
```

CI builds the toolchain on each of the five build machines, then runs `tests/AsmSdk.Tests`
on each: Windows x64 and Arm, Linux x64 and ARM64, and Apple silicon, so every platform's
outputs run natively somewhere. Releases are tags: `vX.Y.Z` publishes to nuget.org through
trusted publishing ([release.yml](.github/workflows/release.yml)).

[`tests/scenarios`](tests/scenarios) holds whole small repositories, one per folder: a
`.slnx` with `.csproj` and `.asmproj` projects, and a `scenario.json` that lists the commands to
run on it (`dotnet build`, `run`, `test`, `publish`, `pack`, `tool install`, the programs they
produce) and what each must print and leave. `ScenarioTests` runs each scenario as one test,
named by its commands, so adding a scenario needs no C#. Our other projects' packages come
from nuget.org at pinned versions (NumSharp, OptunaSharp, KvAsm.Client). Not released yet; see
[Scenario tests](https://asmfoundation.github.io/AsmSdk/ecosystem/scenarios.html).
One scenario runs alone with
`dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj --filter-trait "Scenario=<Name>"`.

[`tests/qa/delivery-matrix.cs`](tests/qa/delivery-matrix.cs) is the delivery QA for .NET
consumers, run by hand before a release rather than in CI, because its Linux cases need
Docker (and QEMU for ARM64). It builds and publishes every way a .NET project gets an asm
library, for every RID and `BinariesCopyPath` layout. Then it checks each output's files,
their object format and `deps.json`, and runs the programs where the machine can. Its report
is `artifacts/qa/delivery/results.md`.

## License

[Apache-2.0](LICENSE). The toolchain packages also contain NASM (BSD-2-Clause)
and LLVM tools (Apache-2.0 WITH LLVM-exception); see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
