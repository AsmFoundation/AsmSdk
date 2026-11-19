# Changelog

All notable changes to AsmFoundation.Sdk and the AsmFoundation.Toolchain
packages. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The toolchain packages carry their own version (`toolchain/versions.json`).
Each SDK release names the one it was tested with.

## [Unreleased]

The SDK itself is unchanged; the samples, the tests and the documentation grew.

### Added

- **Realistic samples,** each a complete program:
  - `samples/Crc32C`: CRC-32C as a freestanding shared library. `ExportsFile` keeps five
    names, versioned `CRC32C_1.0`, and `-z defs` proves nothing is undefined. The first call
    picks one of three paths with `cpuid`: a table, the SSE4.2 `crc32` instruction, or a
    3-way kernel that runs three `crc32` chains and merges them with PCLMULQDQ. NASM's
    preprocessor computes the table and the merge constants (x^(8n−33) mod P).
  - `samples/Crc32C.Net`: its C# API (`[LibraryImport]`, `SuppressGCTransition` for inputs
    up to 4 KiB, `DisableRuntimeMarshalling`), packed as `Samples.Crc32C.Net`, which
    depends on the asm package `Samples.Crc32C`.
  - `samples/Crc32C.Tests` (xunit v3, 21 tests: every path against a bit-at-a-time C#
    reference) and `samples/Crc32C.Bench` (BenchmarkDotNet, against
    `BitOperations.Crc32C`).
  - `samples/consumers/Crc32C.App`: a .NET program that uses the library through the two
    packages, by version.
  - `samples/WordCount`: `wc` on glibc, with libc calls through the PLT, `stderr` through the
    GOT, `warn`/`warnx` added with `AsmLibcSymbol`, and SSE2 counting.
  - `samples/HexDump`: `xxd` without libc, in system calls alone; its output is
    byte-identical to `xxd`'s, from a static executable of about 4 KB.
- **`ExampleTests`** (14 tests) check every fact the new documentation states about these
  samples; AsmSdk.Tests now has 62 tests.
- **Documentation:** an Examples section that walks through the samples, linked from the
  guides; and, for AI tools, `llms.txt`, `llms-full.txt` and a Markdown copy of every page
  (`page.html.md`), written after each site build by `docs/llms.cs` from the same tables of
  contents as the navigation.

### Changed

- `samples/consumer` moved to `samples/consumers/Hello.Consumer`, next to `Crc32C.App`.
- `samples/global.json` selects Microsoft.Testing.Platform for `dotnet test`, which the
  samples' xunit v3 project needs.

## [0.2.0] — 2026-11-18

An `.asmproj` now uses the .NET property of the same purpose wherever one exists, and 0.1.0's
own names are gone, without a compatibility layer. A project
that still sets one fails with an error that names the replacement (`ASM0100` to `ASM0108`),
all of them in one build. Migration:

| 0.1.0 | 0.2.0 |
|---|---|
| `<AsmLibraryName>hello</AsmLibraryName>` | `<AssemblyName>hello</AssemblyName>` (still `libhello.so`, `libhello.a`, `hello`) |
| `<AsmRuntime>Freestanding</AsmRuntime>` (the default) | `<TargetFramework>freestanding</TargetFramework>` (still the default) |
| `<AsmRuntime>Libc</AsmRuntime>` | `<TargetFramework>libc</TargetFramework>` |
| `<AsmDefineConstants>A;B=1</AsmDefineConstants>`, `<AsmDefine Include="C" />` | `<DefineConstants>A;B=1;C</DefineConstants>` |
| `<AsmDebugInfo>true</AsmDebugInfo>` | `<DebugSymbols>true</DebugSymbols>` (`<DebugType>none</DebugType>` turns it off) |
| `<AsmEntryPoint>my_entry</AsmEntryPoint>` | `<StartupObject>my_entry</StartupObject>` |
| `<AsmLinkFlags>-z defs --version-script=x.map</AsmLinkFlags>` | `<LinkerArg Include="-z defs" />` and `<ExportsFile>x.map</ExportsFile>` |
| `<AsmOutputKind>StaticLibrary</AsmOutputKind>` | `<OutputType>StaticLibrary</OutputType>` |
| outputs in `bin/<Configuration>/` | outputs in `bin/<Configuration>/<TargetFramework>/`, as in .NET |

`PackageId` defaults to `AssemblyName`, as in .NET, and 0.1.0's `AsmLibraryName` did not
touch it. Two projects that share an `AssemblyName` (a static and a shared build of one
library) therefore restore with `Ambiguous project name`: give each its own `PackageId`.

### Added

- **IDE integration, like a `.csproj`:** Rider, Visual Studio and VS Code show an `.asmproj`'s
  files, and opening a solution no longer assembles or links anything.
  - **The asm item types are declared** (`AvailableItemName`): IDEs show only the item types
    they know plus those, so in 0.1.0 Rider showed every `.asmproj` empty. `AsmSource` and
    `AsmInclude` are also offered as build actions.
  - **Every other file of the project folder is a `None` item,** as in a `.csproj` (a linker
    script, a C header, a README). `EnableDefaultNoneItems=false`, .NET's own switch, turns it
    off. The managed SDK's `None` glob could not be reused: with no language source extension,
    it removes every file it included.
  - **Design-time builds run no tool.** IDEs build a project's design-time view on every
    load, calling `Compile` and `CoreCompile` with `DesignTimeBuild` and
    `SkipCompilerExecution`. 0.1.0 assembled and linked there: slow loads, races with the
    real build, and errors such as lld's "cannot open libgrpcasm.a" shown as project
    warnings when a referenced project was not built yet. `CoreCompile` now honors both
    properties, as Csc does.
  - **The fast up-to-date checks** of Visual Studio and Rider get `UpToDateCheckInput` (the
    sources, includes and `ExportsFile`) and `UpToDateCheckBuilt` (the output), so editing a
    `.asm` file makes the IDE call MSBuild.
- **The tooling is .NET only.** The end-to-end test is the xunit v3 project
  `tests/AsmSdk.Tests` (`dotnet test`, 48 tests), and the toolchain is staged by
  `toolchain/fetch.cs`, a .NET 10 file-based app (`dotnet run toolchain/fetch.cs -- --rid
  win-x64`). Neither the repository nor CI needs Python.
  - The tests cover what `tests/run.py` checked, one test per expectation, plus IDE
    integration. Through MSBuild they check the declared item types, that every file is
    one item, the up-to-date inputs and outputs, and a replay of Rider's design-time build
    against a control build that does run the tools.
  - `fetch.cs` decodes `.tar.xz` with SharpCompress (.NET has no xz decoder). On the 901 MB
    Windows LLVM archive it took 83.5 s against 78.0 s for Python's lzma, and staged
    byte-identical tools. `VERSIONS.txt` now ends its lines with LF on every build machine.
- **Target frameworks:** `freestanding` and `libc`, and `<TargetFrameworks>freestanding;libc</TargetFrameworks>`
  to build both, exactly like a multi-targeted `.csproj`. Behind the names, freestanding is
  netstandard1.0 and libc is netstandard2.0, so NuGet's own rules say who may use what:
  - a libc project may reference freestanding projects and packages;
  - a freestanding project that references a libc-only project fails at restore with
    `NU1201`, and a libc-only package with `NU1202`;
  - a project or package that targets both gives each consumer the nearest build. A .NET
    project gets the libc build.
- **Implicit preprocessor symbols, as in C#:** `DEBUG` or `RELEASE` (the configuration),
  and `FREESTANDING` or `LIBC` (the target framework), appended after the project body, so
  setting `DefineConstants` never drops them. `DisableImplicitConfigurationDefines` and
  `DisableImplicitFrameworkDefines` turn them off.
- **NativeAOT's names for extra link input:** `LinkerArg` items, and `ExportsFile` for the
  version script (with `--export-dynamic` for an executable, as NativeAOT does). Editing the
  version script relinks.
- **Package layout per target framework:** `build/<framework>/`, `buildTransitive/<framework>/`,
  an empty `lib/<framework>/_._` marker, `native/linux-x64/<tfm>/`, and, when a project
  multi-targets, `runtimes/linux-x64/nativeassets/<framework>/` for its shared libraries.
- **`tests/projects`:** test projects for the compatibility errors, the removed settings,
  `StartupObject`, `ExportsFile`, `LinkerArg`, `DefineConstants`, `DebugSymbols`, a
  multi-targeted project referencing a single-targeted one, and a libc package consumed by
  version. `tests/AsmSdk.Tests` builds them all, and runs the executables.
- **The `Hello.Library` sample** targets both frameworks and reports which build it is
  (`hello_uses_libc`), so `Hello.Net` proves it was given the libc build.
- **The documentation site,** <https://asmfoundation.github.io/AsmSdk/>: guides, the
  complete `.asmproj` reference, the toolchain and the ecosystem. It is built with DocFX
  from `docs/`, with DocFX pinned in `.config/dotnet-tools.json`, and deployed to GitHub
  Pages by `.github/workflows/docs.yml`.

### Changed

- **`docs/asmproj.md` is now the complete reference:** evaluation order, every item and its
  metadata, link lines, the libc stub, and generated files.
- **Two corrections in it:**
  - NASM's messages become MSBuild errors with file and line only with `-Xvc`. `lld`'s
    messages always do.
  - A function missing from the libc stub fails executables, not shared libraries.
- **The package's project URL** points at the documentation site, from the next release.

- **Outputs go to `bin/<Configuration>/<TargetFramework>/`** and `obj/<Configuration>/<TargetFramework>/`,
  as in .NET: `bin/Release/libc/hello-libc` instead of `bin/Release/hello-libc`.
- **Debug information follows `DebugSymbols` and `DebugType`:** DWARF in Debug, none in
  Release, as before; `DebugSymbols=true` adds it to Release, and `DebugType=none` removes it.
- **Every page of the documentation site, the samples and the package readme** use the
  0.2.0 names.

### Removed

- **`tests/run.py` and `toolchain/fetch.py`,** replaced by `tests/AsmSdk.Tests` and
  `toolchain/fetch.cs` (see Added).
- **`AsmLibraryName`, `AsmRuntime`, `AsmDefineConstants`, `AsmDefine` items, `AsmDebugInfo`,
  `AsmEntryPoint`, `AsmLinkFlags` and `AsmOutputKind`.** See the table above.
- **The hidden `netstandard2.0` target framework** of every 0.1.0 project. A project that sets
  it now fails with `ASM0003`.

### Fixed

- **The build targets' comment on `reloc-abs-qword`** said that a shared object always
  resolves a `dq` of a label at load time. That holds only in a writable section: `.data`,
  or `.data.rel.ro` declared `write`. In `.rodata`, or in any section without the write
  flag, the link of a shared library or a Libc executable fails. The comment now says so,
  and explains why the warning can stay off: the linker rejects every case that cannot
  work. Only the comment changed; the build does the same.
- **The guide's jump-table rule** now covers Libc executables, which are PIEs, as well as
  shared libraries. It also says that a Freestanding executable, linked `-static`, accepts
  a `dq` of a label in any section.
- **Multi-targeting never worked in 0.1.0.** The SDK set `TargetFramework` before the
  project body, which turned any `TargetFrameworks` into a single-target build, and
  Microsoft.NET.Sdk gives the outer build of a project with an unknown extension the
  single-target common targets ("Non-CrossTargeting GetTargetFrameworks target should not
  be used in cross targeting (outer) build"). The default is now applied after the body,
  and the outer build gets `Microsoft.Common.CrossTargeting.targets`.
- **A referenced `.asmproj` could be built with the referencing project's
  `TargetFramework`.** `_AsmResolveProjectReferences` now removes the same global
  properties that `ResolveProjectReferences` removes.
- **A 0.1.0 package counted as compatible with every framework,** because it had no
  `lib/` folder. The `lib/<framework>/_._` markers narrow it to the frameworks it was
  built for.

## [0.1.0] — 2026-11-17

The first release.

### Added

- **`.asmproj` projects** (`<Project Sdk="AsmFoundation.Sdk">`). NASM sources
  become a Linux x86-64 shared library, static library or executable, or just
  objects, chosen with the csproj-style `OutputType`.
- **Two runtimes:**
  - `Freestanding` (the default): raw system calls, nothing linked;
  - `Libc`: glibc linked dynamically through a generated `libc.so.6` stub, so
    no sysroot is needed. A `Libc` executable starts in `main` through the
    SDK's `crt1`.
- **References:**
  - `ProjectReference` between `.asmproj` projects adds the include folders and
    links the library;
  - `ProjectReference` from a `.csproj` copies the shared library next to the
    program;
  - `PackageReference` to an asm package adds the package's include folders and
    library.
- **`dotnet pack`** writes native packages:
  - `include/`;
  - `native/linux-x64/*.a`;
  - `runtimes/linux-x64/native/*.so`;
  - optional `src/`;
  - generated `build/` and `buildTransitive/` props.
- **Exact incremental builds:** a rebuild without changes starts no tool; an
  include file change reassembles the sources that can include it.
- **`AsmFoundation.Toolchain` 0.1.0** for `win-x64`, `linux-x64` and
  `osx-arm64`:
  - NASM 3.02: the official binary on Windows, built from source on Linux
    (statically) and on macOS (arm64);
  - LLVM 23.1.5's `lld`, `llvm-ar` and `llvm-readobj`.

  Every download is pinned by SHA-256 and verified before use.
- **Samples and an end-to-end test** (`tests/run.py`) that CI runs on Windows,
  Linux and macOS.
  - It checks every output's ELF properties: type, SONAME, `DT_NEEDED`,
    interpreter, exports and archive members.
  - It runs the executables and a C# program that P/Invokes two asm shared
    libraries.
  - It verifies the incremental builds.

[0.2.0]: https://github.com/AsmFoundation/AsmSdk/releases/tag/v0.2.0
[0.1.0]: https://github.com/AsmFoundation/AsmSdk/releases/tag/v0.1.0
