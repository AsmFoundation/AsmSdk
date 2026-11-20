# Changelog

All notable changes to AsmFoundation.Sdk and the AsmFoundation.Toolchain
packages. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The toolchain packages carry their own version (`toolchain/versions.json`).
Each SDK release names the one it was tested with.

## [0.3.0] — unreleased

One `.asmproj` now builds for **seven runtime identifiers** from any build machine: `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64` and `osx-arm64`. x64
code is NASM (`.asm`), ARM64 code GNU syntax (`.s`) for the bundled `llvm-mc`. A C# project
that references an `.asmproj` gets the binary of the platform it is built or run for, chosen
by .NET's own runtime identifier rules. Toolchain 0.2.0 comes with it: `llvm-mc`, and build
machines for Windows on Arm and Linux ARM64. CI runs on all five build machines, each running
its own platform's outputs.

Migration from 0.2.0, without a compatibility layer:

| 0.2.0 | 0.3.0 |
|---|---|
| no RID: Linux x64 implied | `<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>`, or more. **Required:** a project that lists none is built for what its referencing projects ask for, or for `-r`, or stops with `ASM0010`, which names all three ways |
| outputs in `bin/<Configuration>/<tfm>/` | outputs in `bin/<Configuration>/<tfm>/<rid>/`, as for `dotnet build -r` |
| `AsmTargetRid`, `AsmFormat` (fixed at `linux-x64`, `elf64`) | removed: `RuntimeIdentifier` decides the format and the linker |
| packages with `native/linux-x64/<tfm>/` and `runtimes/linux-x64/native/` | `native/<rid>/<tfm>/` and `runtimes/<rid>/native/` for every RID the project builds |
| `global f:function`, `call f wrt ..plt`, `section .note.GNU-stack` in every source | the same in Linux-only files. A source for several OSes uses `asmsdk.inc` (`FUNCTION f`, `call f PLT`) and wraps the note in `%ifdef LINUX`: macho64 stops on all three |
| `call helper wrt ..plt` (or a `hidden` helper) for a call within one `.so` | a plain `call helper` links: Linux shared libraries are linked `-Bsymbolic`. The PLT is for other libraries |

### Added

- **Runtime identifiers, as in .NET.** `RuntimeIdentifiers` lists the platforms; each is an
  inner build with the global `RuntimeIdentifier`, and a build without one (the RID outer
  build) builds each, the way a multi-targeted project builds each framework. Each inner
  build prints `<Project> -> <output>`. Errors `ASM0010` (no RID), `ASM0011` (a RID outside
  the seven), `ASM0012` (a RID the project does not list). In a solution build, a project
  that lists no RID builds nothing by itself, with a message.
- **Sources per RID, by file name.** The extension is the language: `.asm` (NASM) builds the
  x64 RIDs, `.s` (GNU syntax, llvm-mc) the ARM64 ones. A qualifier before the extension
  narrows a file: a RID (`x.win-x64.asm`), an OS family (`win`, `linux`, `linux-musl`,
  `osx`), `unix`, or a CPU (`x64`, `arm64`). For each RID, the most specific file of a name
  wins. Errors `ASM0020` (a tie), `ASM0021` (a NASM file for ARM64 only), `ASM0023` (a RID
  no source builds), `ASM0024` (a source listed twice). `AsmLlvmMcFlags` and
  `AsmLlvmMcCommand` for llvm-mc.
- **The platform layer, on every include path:**
  - `asmsdk.inc` (NASM): `FUNCTION`, `LOCAL_FUNCTION` and `HIDDEN_FUNCTION` (exported,
    file-local, global but exported from no image), `RETURN`, `ENDFUNCTION`, with exports per
    object format and the callee-saved registers each OS expects; `ARG1`..`ARG6` (and their
    32-, 16- and 8-bit forms), `SHADOW_SPACE`, `PLT`.
  - `asmsdk-gnu.inc` (llvm-mc): `FUNCTION`, `LOCAL_FUNCTION`, `HIDDEN_FUNCTION`, `ENDFUNCTION`,
    `CALL`, `TAILCALL`, `ADDRESS`, `RODATA`.
  - Defines `WINDOWS`, `LINUX`, `MACOS`, `MUSL`, `X64`, `ARM64`; `ASMSDK_EXPORTS_FILE` with
    an `ExportsFile`, and `ASMSDK_EXECUTABLE` in a program.
- **`TargetFramework=libc` on every RID,** with no sysroot or platform SDK: the SDK writes the
  stand-ins from symbol lists in `stubs/` (`AsmWriteCLibraryStub`).
  - Linux: a stub `libc.so.6` (glibc) or `libc.musl-x86_64.so.1` (musl, with musl's loader),
    and `libasmsdk_nonshared.a` with `atexit`, which glibc keeps out of `libc.so.6`. `crt1`
    for x64 and ARM64.
  - macOS: `libSystem.tbd`; dyld calls `main` (`LC_MAIN`). `AsmMacOSMinimumVersion`
    (default 11.0).
  - Windows: `ucrtbase.lib` (the Universal C Runtime) with `mainCRTStartup`, and
    `asmsdk_winstdio.lib`: `printf`, `fprintf`, `sprintf`, `snprintf`, their `v` forms,
    `sscanf` and `atexit`, which the UCRT headers define inline and `ucrtbase.dll` does not
    export.
  - kernel32 is linked into every Windows output, the stable way to the OS (Windows system
    call numbers change between releases).
  - `AsmLibcSymbol` adds names on every OS. A macOS freestanding program is `ASM0015`: Apple
    supports no static executables.
- **One `ExportsFile` for every OS:** a version script on Linux, `-export:` names on Windows
  (and `FUNCTION` then writes no directive), `-exported_symbol` on macOS. `ASM0030` for what
  only ELF can express.
- **.NET consumers get the right binary.** Every `.asmproj` carries an implicit
  `PackageReference` to `AsmFoundation.Sdk`, whose `buildTransitive` targets reach the .NET
  projects that reference it:
  - a portable build gets every RID's library under `runtimes/<rid>/native/`, listed in
    `deps.json` (`ASM0042`), so the .NET host loads the right one;
  - a RID-specific build gets the RID that serves it best by the .NET SDK's RID graph, copied
    flat (`ASM0040`, `ASM0041`);
  - `<ProjectReference … RuntimeIdentifiers="a;b" />` narrows the RIDs built;
  - static libraries, programs and objects are skipped, and the app's publish properties
    (`SelfContained`, `PublishAot`, …) are not passed on;
  - `DisableAsmConsumerTargets` turns it off.
- **Packages for every RID:** `runtimes/<rid>/native/` and `native/<rid>/<tfm>/` (static
  libraries, and the import libraries of Windows DLLs). The generated props give an asm
  consumer its RID's build by the RID graph, or `ASM0060`. The implicit SDK dependency is
  removed from the `.nuspec` (`ASM0050`).
- **Image checks** (`AsmCheckImage`), after each Windows and macOS link, for load-time fixups
  the loader cannot apply but the linker accepts: `ASM0080` for a 32-bit absolute address in a
  64-bit Windows image (lld-link links a DLL based at 0x180000000 with one, silently; MSVC's
  linker refuses it), `ASM0081` for a fixup in a read-only Mach-O segment (a text relocation,
  from the rebase or bind opcodes or the chained fixups), `ASM0082` for an unreadable image.
  A failed check deletes the image, so the next build checks again.
- **Toolchain 0.2.0:** `llvm-mc`; packages for `win-arm64` (native LLVM, NASM's x64 build
  under emulation) and `linux-arm64` (NASM built from source). Intel Macs and musl hosts have
  no package (LLVM publishes no binaries for them): `ASM0006` names `AsmToolchain=System`.
  `ASM0004` for a `.s` source with a toolchain without `llvm-mc`, `ASM0005` for a Windows or
  macOS RID, or the Linux RID of the other architecture, with the System toolchain's GNU `ld`.
- **Realistic samples,** each a complete program:
  - `samples/Crc32C`: CRC-32C as a freestanding shared library for all seven RIDs: x64 in
    NASM through `asmsdk.inc`, ARM64 in GNU syntax. `ExportsFile` keeps five names (versioned
    `CRC32C_1.0` on Linux). The first call picks one of three paths: a table, the hardware
    CRC instruction (SSE4.2 `crc32`, ARM64 `crc32cx`), or a 3-way kernel merged with
    PCLMULQDQ or PMULL. The assemblers compute the table and the merge constants
    (x^(8n−33) mod P). ARM64 detection per OS: the ID registers on Linux,
    `IsProcessorFeaturePresent` on Windows, always the 3-way path on Apple silicon.
  - `samples/Crc32C.Net`: its C# API (`[LibraryImport]`, `SuppressGCTransition` for inputs
    up to 4 KiB, `DisableRuntimeMarshalling`), packed as `Samples.Crc32C.Net`, which
    depends on the asm package `Samples.Crc32C`.
  - `samples/Crc32C.Tests` (xunit v3, 21 tests: every path against a bit-at-a-time C#
    reference) and `samples/Crc32C.Bench` (BenchmarkDotNet, against
    `BitOperations.Crc32C`), both on every platform.
  - `samples/consumers/Crc32C.App`: a .NET program that uses the library through the two
    packages, by version.
  - `samples/WordCount`: `wc` on glibc, with libc calls through the PLT, `stderr` through the
    GOT, `warn`/`warnx` added with `AsmLibcSymbol`, and SSE2 counting.
  - `samples/HexDump`: `xxd` without libc, in system calls alone; its output is
    byte-identical to `xxd`'s, from a static executable of about 4 KB.
- **Tests:** `CrossPlatformTests` (formats, exports, qualifiers, the C library of each OS,
  programs' exports, the .NET consumer per RID, packages, the RID errors, the image checks,
  each machine's own build run natively, the x64 build under emulation on ARM64 machines,
  `-o`), `DotnetAppTests` (a .NET program that reaches the asm project through a class
  library: RID-specific build and publish, `-o`, single file with the library beside it and
  inside it, the publish switches on the command line), `DiagnosticsTests` (NASM's errors at
  their line, `ASM0005` for the System toolchain), `InteropTests` (the interop tour,
  `tests/projects/Interop`: every way C# calls an asm library, 51 checks on x64 and 48 on
  ARM64, run natively on each of the five CI machines), and `ExampleTests` for every fact the
  documentation states about the samples. AsmSdk.Tests has 135 tests.
- **Documentation:** a Platforms and runtime identifiers guide; every guide, the reference and
  the toolchain pages for the seven RIDs; an Examples section that walks through the samples;
  and, for AI tools, `llms.txt`, `llms-full.txt` and a Markdown copy of every page
  (`page.html.md`), written after each site build by `docs/llms.cs`.

### Changed

- **Linux shared libraries link `-Bsymbolic`:** a library's references to its own global
  symbols bind to the library, as on Windows and macOS. `<LinkerArg Include="-Bno-symbolic" />`
  restores ELF's interposition.
- **Programs export nothing** without an `ExportsFile`: `FUNCTION main` made `main` an export,
  and a Windows `.exe` then got a stray import library. A Windows program links `-noimplib`
  unless its `ExportsFile` exports something.
- **NASM reports in Visual C++'s format** (`-Xvc`): `x.asm(12): error : …`. MSBuild recognizes
  it, so an error or warning is an MSBuild one with its file and line, which IDEs list and
  open. NASM's default `x.asm:12: error:` (no column) was plain output: the IDE showed only
  `ASM0007` and `MSB3073`. A NASM warning now counts as an MSBuild warning, so a build with
  `-warnaserror` fails on one.
- **`dotnet build -o <folder>`** of an asm project with several RIDs puts each RID in
  `<folder>/<rid>/` (`<folder>/<tfm>/<rid>/` when it targets both frameworks), and so does an
  explicit `OutDir`. A global output folder is never extended by the RID, and the RIDs
  overwrote each other's files.
- **The default sources** are `**/*.asm` and `**/*.s`.
- **`AsmToolchainVersion`** defaults to 0.2.0.
- The Crc32C sample's paths are named for every CPU: `crc32c_append_hardware` and
  `crc32c_append_hardware_3way`, and `Crc32CPath.Hardware` and `HardwareThreeWay` in C#.
- `samples/consumer` moved to `samples/consumers/Hello.Consumer`, next to `Crc32C.App`.
- `samples/global.json` selects Microsoft.Testing.Platform for `dotnet test`, which the
  samples' xunit v3 project needs.

### Fixed

- **A glibc program that called `atexit` linked but did not start:** glibc keeps `atexit` in
  `libc_nonshared.a`, not `libc.so.6`. The SDK now links its own.
- **A `ProjectReference` to an `OutputType=Objects` project** failed to link (`cannot open
  <referencing project>/obj/…/x.o`): the object paths were relative to the wrong folder.
- **A failed assembler run** could leave an empty object that later builds took as up to date.
  The object is now deleted, and the build reports `ASM0007` with the source's name.

Found by a manual QA of C# with asm projects (2026-11-20: a solution of three asm projects and
three .NET projects, every interop technique, every build and publish mode, run on Windows,
Ubuntu, Alpine, Linux ARM64 and a clean Windows 11 with only the .NET SDK):

- **A single-file publish lost the asm library.** `dotnet publish -r win-x64
  -p:PublishSingleFile=true` put it neither in the bundle nor beside it (`NETSDK1236`), and
  the program failed with `DllNotFoundException`. On Linux and macOS,
  `IncludeNativeLibrariesForSelfExtract` left `lib<name>.so` and `.dylib` outside the bundle.
  The .NET SDK decides by `AssetType`, which `GetCopyToPublishDirectoryItems` drops: the
  consumer targets now mark the asm libraries `AssetType=native`, as NuGet marks a package's.
  The library goes beside the bundle, or inside it with `IncludeNativeLibrariesForSelfExtract`.
- **A RID-specific build or publish through a class library** (app → C# library → `.asmproj`)
  carried every RID's `runtimes/<rid>/native/` copy. The class library is RID-agnostic, so it
  is built portable and hands them on: 11 dead files in the QA solution, and Windows and macOS
  libraries in Linux container images. They are now left out, as NuGet leaves out other RIDs'
  assets.
- **`dotnet build -r <rid> -o <folder>` through a class library failed to link:** the global
  output folder reached the asm projects, every RID of the portable build wrote into it, and a
  link read another machine's static library ("machine type x64 conflicts with arm64"). The
  consumer targets no longer pass the app's output folder (`OutputPath`, `OutDir`) to asm
  projects, which build in their own `bin/`; the app's folder gets the copies.
- **`dotnet publish -p:PublishAot=true` or `-p:PublishTrimmed=true`** stopped NuGet's restore in
  every asm project (`NETSDK1207`, `NETSDK1124`): a command-line property reaches every project
  of the restore graph, where no consumer target can remove it. `Sdk.props` now declares the
  .NET publish switches `TreatAsLocalProperty` and sets them to `false`.
- **`AsmToolchain=System` for the Linux RID of the other architecture** failed in GNU `ld` with
  "file in wrong format" (an x64 machine's `ld` links x64 ELF only). It stops with `ASM0005`,
  which names `ld.lld` or a cross `ld`.

### Removed

- `AsmTargetRid` and `AsmFormat`: the RID decides the platform and the object format.
- `stubs/libc.asm`: the C library stand-ins are written from the symbol lists.

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
