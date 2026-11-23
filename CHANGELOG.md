# Changelog

All notable changes to AsmFoundation.Sdk and the AsmFoundation.Toolchain
packages. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The toolchain packages carry their own version (`toolchain/versions.json`).
Each SDK release names the one it was tested with.

## [Unreleased]

### Added

- **WebAssembly, the third supported target** (the owner's choice of 2026-11-23, from a measured
  ranking of six candidates). Two new RIDs, under .NET's names: `browser-wasm` and `wasi-wasm`.
  The code is LLVM's WebAssembly assembly in `.s` files qualified for it, and the bundled
  `llvm-mc` and `wasm-ld` build it on every build machine.
  - **Sources:** `name.wasm.s` (both RIDs), `name.browser.s`, `name.wasi.s`, or the RID itself.
    No unqualified file builds WebAssembly. `unix` stays Linux and macOS.
  - **Outputs:** a `Library` is a module (`name.wasm`, exporting its `FUNCTION`s or its
    `ExportsFile`'s names, and its memory) plus an archive (`name.a`; `AsmProduceStaticLibrary`
    is `true` by default there). A `StaticLibrary` is the archive. A `wasi-wasm` program is a
    WASI command entered at `_start`. Names carry no `lib` prefix.
  - **A library's archive holds what its module holds:** its objects, and the static libraries
    and object files it links (llvm-ar's MRI `ADDLIB`). A native shared library contains them
    the same way, which is why its static library references are private by default, and a .NET
    app or a package consumer needs only the one archive. Measured: the merged archive links
    alone, and next to the original static library in either order, without a duplicate symbol.
  - **`asmsdk-wasm.inc`**, the WebAssembly platform layer, which ImplicitUsings and
    `asmsdk-gnu.inc` bring:
    - `FUNCTION name, (params) -> (results)`, `LOCAL_FUNCTION`, `HIDDEN_FUNCTION`, `ENDFUNCTION`;
    - `IMPORT_FUNCTION module, name, sig`, `EXTERN_FUNCTION`;
    - `CALL`, `TAILCALL` (a call and a return), `ADDRESS`;
    - `RODATA`, `DATA`, `BSS`, `ENDDATA`.
  - **Defines** `WASM`, `BROWSER` or `WASI` (and the usual ones) reach a wasm source as labels.
    llvm-mc's wasm object writer stops on any constant symbol, `--defsym` included (measured:
    "WASM: absolute addressing not supported!").
  - **Features:** llvm-mc accepts SIMD128 and tail calls (`-mattr=+simd128,+tail-call`). It
    writes no `target_features` section, so a module needs them only if its code uses them.
  - **.NET WebAssembly apps link asm projects.** A `browser-wasm` app (Blazor WebAssembly,
    `wasmbrowser`, `wasmconsole`) or a `wasi-wasm` app that references an asm library, static
    library or object files project gets their archive or objects as `NativeFileReference`
    items. Its build links them into its runtime (Emscripten, or the WASI SDK). The P/Invoke
    module is the asm project's `AssemblyName`, so `[LibraryImport("calc")]` works as
    elsewhere. For .NET 10's WASI build, the consumer targets also name the module, which that
    build otherwise leaves out of its P/Invoke table (`DllNotFoundException`, measured with
    10.0.15). A package of a wasm library does the same through its props, under
    `native/<rid>/<tfm>/` (nothing under `runtimes/`).
  - **`dotnet run` starts a `wasi-wasm` program in wasmtime** (from `PATH`) after the machine's
    own builds. `AsmRunIn=Wasi` asks for that alone.
  - **New diagnostics:**
    - `ASM0016`: a `libc` module or WASI program; the bundled wasm-ld links no C library.
    - `ASM0017`: a program for `browser-wasm`.
    - `ASM0018`: a define with a value, which a label cannot carry.
    - `ASM0019`: llvm-mc's wasm writer crashed. It names the source and the usual three causes:
      a `.set` constant, a data label without a size, a function outside its own section.
    - `ASM0026`: a source cannot be read for its wrapper.
    - `ASM0021` and `ASM0023` gained their WebAssembly forms.
    - `ASM0030` refuses a pattern in a module's exports file.
    - `ASM0005` stops a System toolchain without `AsmLinkerCommand` (`wasm-ld`).
    - `ASM0054`: a .NET WebAssembly app links asm projects, or an asm package, and .NET has no
      native WebAssembly build for the app's .NET version. The message names the fix. Without
      that build .NET compiles the app as plain IL, ignores `NativeFileReference`, and says
      nothing. Measured on GitHub's windows-2025 image: SDK 11.0.100 with Visual Studio's
      `wasm-tools`, which serves `net11.0`, built a `net10.0` app with no `AppBundle`. Fixes:
      - `wasm-tools-net10` for a browser app on a later SDK;
      - the app's own SDK for a WASI app: .NET 11.0.100's WASI build serves only `net11.0`.
  - **Measured end to end** in `WebAssemblyTests`, with .NET 10.0.15, Node 22 and wasmtime 39:
    - the modules run in Node;
    - the WASI program runs in wasmtime and through `dotnet run`, and exits with its
      `proc_exit` code;
    - a .NET browser-wasm app links a library, a static library and object files, from
      projects and from a package, and runs in Node;
    - a .NET wasi-wasm app links them and runs in wasmtime.

    Traps found on the way, each documented in the WebAssembly guide:
    - .NET's browser build runs wasm-opt without tail calls, so it refuses `return_call`;
    - a bare `.text` does nothing in llvm-mc's wasm parser;
    - `.bss` and `.rodata` are unknown directives;
    - `.int32` takes one value;
    - wasmtime refuses misaligned WASI pointers.
- **Programs get the asm shared libraries they link.** A program that links an asm shared
  library (a `ProjectReference`, or a package) gets it in each RID's output folder, with the
  run path that finds it there:
  - Linux: `DT_RUNPATH` `$ORIGIN`;
  - macOS: `LC_RPATH` `@loader_path`;
  - Windows: the program's own folder, which the loader searches first.

  On Windows the program links the import library, and the DLL behind it is copied: referenced
  projects and packages now name that file (`AsmRuntimeFile` metadata).
  - `BinariesCopyPath` places the libraries, as it does for a .NET app: the reference's value,
    then the property, then `Auto`, which for a program means its own folder. The run path
    follows the folder (`$ORIGIN/lib/linux-x64`).
  - On Windows a folder other than the program's own is `ASM0047`, since Windows has no run
    path.
  - A program that links no shared library is linked as before.
  - `ASM0048` (a warning) names an import library whose DLL a 0.3.0 package does not name.

  Tests no longer set `LD_LIBRARY_PATH` to run such a program.
- **`dotnet run` runs asm programs.** It builds the program and starts the build this
  machine can run, with the arguments after `--` unchanged. The order:
  1. this machine's own RID, or another build it runs natively (on Linux, one whose loader is
     installed);
  2. one it emulates: `win-x64` on Windows on Arm, `osx-x64` under Rosetta 2;
  3. on Windows, a Linux build through WSL;
  4. a Linux build in a Docker container (`debian:stable-slim`, `alpine:3`, another CPU under
     QEMU).

  `dotnet run -r <rid>` runs that RID. Settings: `AsmRunIn` (`Auto`, `Host`, `Wsl`,
  `Docker`), `AsmRunWslDistribution`, `AsmRunDockerImage`, and `RunArguments` and
  `RunCommand` as in .NET. Errors: `ASM0090` (not a program), `ASM0091` (nothing here can
  run any build, with what was tried), `ASM0092` (not built), `ASM0093` (unknown `AsmRunIn`).
  Measured on Windows x64: `win-x64` natively, `linux-x64` through WSL, `linux-musl-x64`,
  `linux-arm64` and `linux-musl-arm64` in Docker, with quotes and spaces in the arguments
  intact.
- **`dotnet publish` publishes asm programs and libraries:**
  - `-r <rid>` publishes that RID into .NET's folder (or `-o`);
  - without `-r`, every RID the project lists, each into `publish/<rid>/` (`-o <folder>`
    gives `<folder>/<rid>/`), or only the RIDs a referencing project asks for.

  A publish holds the output, its PDB, a program's libraries where `BinariesCopyPath` put
  them, and the files marked `CopyToPublishDirectory` or `CopyToOutputDirectory`. It holds
  nothing the managed pipeline would add: not `obj/<name>.dll` (the MSB3030 of 0.3.0), and
  not a package's `runtimes/` files copied flat. Release by default, as for .NET 8 projects
  (`PublishRelease`).
- **A program as a dependency, the helper-process case:**
  - A .NET project that references an asm program, or its package, gets it with the libraries
    it links. A portable build gets every RID's program in `runtimes/<rid>/native/`, listed in
    `deps.json`; a RID-specific build gets its RID's next to the app. `BinariesCopyPath`
    places it, and its libraries keep their place relative to it, because its run path
    depends on that. The app finds it through `NATIVE_DLL_SEARCH_DIRECTORIES` (the
    [guide](https://asmfoundation.github.io/AsmSdk/guide/programs.html) has the code).
  - An asm program that references an asm program copies it, with its libraries, where its
    `BinariesCopyPath` says, on Windows too.
  - `dotnet pack` of a program puts each RID's program and libraries in
    `runtimes/<rid>/native/`, with props that deliver it to an asm consumer
    (`AsmReferencedProgram` items).
  - `PrivateAssets="all"` on a .NET library's reference to a program puts the program inside
    the library's package.
  - A program's own references default to `PrivateAssets="all"`, since it carries what it
    needs. A consumer no longer gets the program's libraries a second time as references of
    its own, and the program's package depends on nothing.
  - A packed program keeps its libraries next to it: NuGet copies `runtimes/` files flat into
    an app built for one RID. Libraries in a subfolder stop the pack with `ASM0051`.
  - Two files with the same bytes on one path (a library that the app references and that a
    program's bundle also holds) are one file, not an `ASM0044`.

  Measured: a .NET app (portable, RID-specific, and `tools/{rid}`) and an asm program started
  or carried the program, from a project reference, its package, and a .NET library's package.
- **`PackAsTool` packs a program as a .NET tool,** in the package kinds the .NET 10 SDK writes
  for a platform-specific tool:
  - the top-level `DotnetTool` package (`tools/any/any/`) names the command and the package of
    each RID;
  - one `DotnetToolRidPackage` per RID (`tools/any/<rid>/`) holds the program, its libraries
    and data files, with `Runner="executable"`.

  `dotnet tool install -g <id>` installs the machine's package, and the command starts the
  program, with no .NET runtime. `ToolCommandName` names the command. `dotnet pack -r <rid>`
  packs one RID's package. Errors: `ASM0052` (settings file), `ASM0053` (not a program, or
  several target frameworks). Measured: installed and run on Windows x64, and in the .NET SDK
  containers for glibc and Alpine, where the command is a symbolic link into the tool store
  and `$ORIGIN` still finds the library.

- **`linux-musl-arm64`, the eighth runtime identifier:** musl distributions such as Alpine on
  ARM64 (AWS Graviton, Ampere, Linux VMs on Apple silicon). It works like `linux-musl-x64`:
  - ARM64 code is assembled for llvm-mc's `aarch64-linux-musl`, with the `MUSL` define;
  - a qualified `.s` file builds every RID its qualifier names, x64 ones too (as GNU-syntax
    x86-64). So a `.s` qualified `linux-musl` now applies to both musl RIDs, and musl ARM64
    code beside a `name.linux-musl.asm` takes the RID qualifier, `name.linux-musl-arm64.s`.
    Otherwise both files claim `linux-musl-x64`, and ASM0020 names the fix;
  - a `libc` build links against musl's `libc.musl-aarch64.so.1`, and its programs start
    with `/lib/ld-musl-aarch64.so.1`. The SDK's ARM64 `crt1` and `atexit` serve musl as they
    serve glibc: musl's `__libc_start_main` takes the same first three arguments and ignores
    the rest.

  A .NET app built for `linux-musl-arm64` now gets that build itself. Until now NuGet's
  fallback gave it the `linux-arm64` build, which serves code without the C library but
  names glibc's `libc.so.6` otherwise. Measured on Alpine ARM64 (under QEMU): a `libc`
  program, and a .NET app that loads the RID's library.
- **`tests/qa/delivery-matrix.cs`, the delivery QA matrix that proved 0.3.0,** as a .NET
  file-based app: `dotnet run tests/qa/delivery-matrix.cs`. It covers every way a .NET
  project gets an asm library:
  - a direct reference, or one through a class library, with or without `PrivateAssets="all"`;
  - the asm package itself;
  - a .NET package with the binaries inside, or one that depends on the asm package.

  Each is built and published for every RID, in every layout. Every case is then checked by
  file, by object format (llvm-readobj), by `deps.json`, and by running the program wherever
  the machine can: natively, in Linux containers, and for ARM64 under QEMU. On a Windows x64
  machine with Docker that is 94 cases with `linux-musl-arm64` (84 before it), all passing,
  with a negative control (NativeAOT without the generated loader must fail).
- **Scenario tests (`tests/scenarios`): whole small repositories, built and run as a user
  would.** Each folder is a `.slnx` with `.csproj` and `.asmproj` projects, plus a
  `scenario.json` that lists the commands to run on it. The commands can be `dotnet build`,
  `run`, `test`, `publish`, `pack` and `tool install`, or the programs they produce. For each
  command the file says the exit status, the output and the files it must leave. `ScenarioTests`
  runs every scenario as one test, in a fresh copy under `artifacts/scenarios`. The test's name
  lists every command, and its log shows each with its output. Adding a scenario needs no C#,
  and `--filter-trait "Scenario=<Name>"` runs one alone. The first eight:
  - a C# app with an asm library;
  - xunit unit tests of asm code (`dotnet test`);
  - an asm program that links an asm library;
  - NumSharp arrays computed on by asm kernels;
  - `KvAsm.Client` talking to the KvAsm server image, with an asm checksum;
  - an OptunaSharp study that tunes an asm function;
  - a C# app that starts an asm program it references;
  - an asm program packed as a .NET tool and installed.

  Our other projects' packages come from nuget.org at the versions pinned in
  `tests/scenarios/Directory.Packages.props`, so the scenarios also restore under central
  package management. See
  [Scenario tests](https://asmfoundation.github.io/AsmSdk/ecosystem/scenarios.html).
- **`dotnet new` templates: the `AsmFoundation.Templates` package,** released with the SDK under
  the same version:
  - `asmlib`: a shared library, x64 code in NASM and ARM64 code in GNU syntax;
  - `asmexe`: a program on the platform's C library, which `dotnet run` builds and runs;
  - `asmstatic`: a static library with a public `include/` folder;
  - `asmnet`: a `.slnx` with a C# console app, the asm library it calls through
    `[LibraryImport]`, and xunit v3 tests of that library.

  The projects name the SDK version on their first line
  (`<Project Sdk="AsmFoundation.Sdk/<version>">`), so they need no `global.json`, as
  `dotnet new mstest --sdk` writes `MSTest.Sdk/4.2.3`. The version is the templates package's,
  the newest at install; `--sdk-version` picks another. MSBuild takes an exact SDK version only:
  `*`, `0.*`, `latest` and no version all fail (measured). `asmnet --test-runner mtp-v2|vstest`
  mirrors xunit's own `xunit3` template: `mtp-v2`, the default, sets Microsoft.Testing.Platform
  in `global.json`. A name with a hyphen keeps it in files, paths and the library name
  (`my-app.Native`), and becomes `my_app` in the C# namespace. The two plug-and-play scenarios
  build, run, test (both runners), `sln add`, publish and pack what the templates create.
- **A project that lists no `RuntimeIdentifiers` builds for the build machine's RID** (the
  owner's choice). It used to stop with `ASM0010`: so did `dotnet run` of a C# app that
  referenced it, and a solution build went green while the app got no native library at all.
  The order is now `-r`, a referencing project's RIDs, the project's own list, then the
  machine's RID: `win-x64`, `linux-musl-arm64` and so on. `ASM0010` remains for a machine that
  is none of the eight.
  - **`ASM0013` (a warning) says when that one RID leaves the machine:** a `dotnet pack` of the
    project, or a portable `dotnet publish` or `dotnet pack` of a .NET project that references
    it. The message names the line to add. A publish with `-r` does not warn, and
    `<NoWarn>ASM0013</NoWarn>` silences it.
- **`ImplicitUsings`, on by default:** every source gets the SDK's platform layer without
  writing it. NASM gets `include/asmsdk-implicit.inc` as its pre-include (`-P`): `asmsdk.inc`,
  `default rel`, on Linux the empty `.note.GNU-stack` section, and `section .text` (see Fixed).
  Each `.s` source is assembled
  through a two-line file in `obj/` (`.include "asmsdk-gnu-implicit.inc"`, then the source),
  because llvm-mc has no pre-include; messages still name the source and its lines. Sources that
  write these lines themselves build as before, because both headers have guards.
  `<ImplicitUsings>disable</ImplicitUsings>` gives raw NASM back. A source that relied on NASM's
  implicit `default abs` (which NASM 3 warns about) now gets `default rel`: write `default abs`
  in it, which wins, as the test project for `ASM0080` now does.
- **.NET's warning switches reach NASM by warning name.** `NoWarn` gives `-w-<name>`,
  `WarningsAsErrors` gives `-w+error=<name>`, and `WarningsNotAsErrors` gives `-w-error=<name>`.
  They are passed after `-w+error`, so they win. Only entries shaped like NASM's names (lower
  case, hyphens) are passed on, so one list can also hold `NU5128` or `CS1591`. Before, this
  took `<AsmFlags>-w-label-orphan</AsmFlags>`.
- **Reproducible Release builds.** NASM and llvm-mc get each source by its path relative to the
  project folder, and the SDK's own startup and C library sources by file name, in their folder.
  NASM also runs with `--reproducible`. The same projects built in two checkout folders, each
  with its own NuGet cache, give byte-identical outputs for all eight RIDs: freestanding and
  libc shared libraries, static libraries, and libc programs. Before, ELF file symbols and COFF
  `.file` records held the checkout's path, and a Linux libc program held the NuGet cache's.
  Debug information still records the folder, because NASM 3.02 has no prefix map.
- **`dotnet sln add` takes an `.asmproj`.** The SDK sets `DefaultProjectTypeGuid` to the
  generic project type, and the CLI writes it as the entry's `Type`. Before, the CLI printed
  "unknown project type", added nothing, and still exited 0. `dotnet add reference` still
  refuses an `.asmproj` ("incompatible targeted frameworks"): the CLI parses the
  `TargetFramework` text and does not know NuGet's aliases
  ([dotnet/sdk#57317](https://github.com/dotnet/sdk/issues/57317), reproduced there with plain
  C# projects). The plug-and-play scenario checks for this, so it fails the day the CLI accepts
  one.
- **An assembler option set outside the project file assembles again.** The options go to
  `obj/.../asm/assemble-options.txt`, rewritten only when they change, and the assemble step
  depends on that file, as C#'s compile depends on `CoreCompileInputs.cache`. Before, a
  `DefineConstants`, `NoWarn` or `AsmFlags` set in `Directory.Build.props` or with `-p:`
  linked the objects assembled without it.
- **Generated C# bindings.** A C# project (.NET 7 or later) that references an asm library with
  `PROTO` lines calls it with nothing written by hand: the SDK package's Roslyn incremental
  generator (`bindings/netstandard2.0/AsmFoundation.Sdk.Bindings.dll`, added by the consumer
  targets with each reference's API file as an `AdditionalFiles` item) writes a class named
  after the library (its `AssemblyName` in PascalCase) in the project's `RootNamespace`, one
  nested class per `.asm` file with folders as levels; a function named like its file is
  `Invoke`. `[AsmLibrary]` (generated into the project, `AsmFoundation.Interop`) sets options
  for every library and per library on the assembly (`Nesting`, `SpanOverloads`, `Visibility`,
  `ClassName`), and puts a library, chosen `Files` or a `Folder` into an app's `static partial`
  class, overriding both; each function is bound once unless a class says `AliasBinding`. The
  bindings are blittable `[DllImport]` externs (`ExactSpelling`, `[SuppressGCTransition]` for
  `PROTO fast`) with generated conversions: a `bool` as one byte, a `const char*` parameter as a
  UTF-8 `string` through `Utf8StringMarshaller`'s stack buffer, a function pointer as
  `delegate* unmanaged<...>`, and a span overload for each pointer and length pair (`len`,
  `length`, `size`, `count`, `<pointer>_len`, `<pointer>_count`). Not `[LibraryImport]`: source
  generators never see each other's output, so .NET's LibraryImport generator left generated
  `[LibraryImport]` methods without a body (measured: CS8795). The SDK sets `AllowUnsafeBlocks`
  whenever bindings are generated (the .NET SDK defaults it to `false`, measured);
  `AsmGenerateBindings=false` turns the bindings off. Libraries only: a referenced asm program
  gets none, `PROTO` lines or not (it is started, never loaded), and a .NET WebAssembly app
  none yet (the wasm RIDs write no API file). Errors at the attribute: **ASM0140** (no
  such library), **ASM0141** (no such file or folder), **ASM0142** (bound twice), **ASM0143**
  (not `static partial`, or a name generated twice), **ASM0144** (an unreadable API file). Phase 2
  of the design; also fixed: `GetAsmApiFile` returns absolute paths (a referencing project read
  relative ones against its own folder).
- **`PROTO` lines and the API file, the first phase of generated C# bindings.** A
  `PROTO int32_t add_ints(int32_t a, int32_t b)` line beside a `FUNCTION` (NASM and GNU syntax
  alike; `PROTO fast ...` for a short function that never blocks) declares its C signature.
  `PROTO` and every function macro write a text record into a metadata section of the object
  (`.asmapi`: an info section on Windows and a `__DWARF` debug section on macOS, which the
  linkers drop; a non-allocated section in a Linux `.so`, never loaded). After assembling each
  RID, `AsmReadApi` reads the records, parses the prototypes (the fixed-width C types, `bool`,
  `char`, `float`, `double`, pointers and function pointers; not `int` or `long`, which differ
  between platforms) and checks them: **ASM0130** (does not parse, or a type it cannot bind),
  **ASM0131** (no exported `FUNCTION` of that name; a static library may describe a
  `HIDDEN_FUNCTION`), **ASM0132** (the integer and pointer parameters differ from `FUNCTION`'s
  count), **ASM0134** (two `PROTO` lines for one function), **ASM0135** (unreadable records),
  each at the `PROTO` line. The RID's `<AssemblyName>.api.json` goes into `obj/` and beside its
  binary; the RID outer build's `AsmMergeApi` checks that every RID declares the same functions
  the same way (**ASM0133**, naming each RID's prototype and source) and writes the library's
  file in `bin/<Configuration>/<tfm>/`. No `PROTO` line, no file. The WebAssembly RIDs (also
  unreleased) take no part yet: their objects carry no records, and a `PROTO` line in a wasm
  source is an error. The design (the owner's choices
  of 2026-11-23, approved as a spec): catalog `asmsdk/plans/csharp-bindings.md`; the C# generator
  is the next phase.
- **A NativeAOT publish links a referenced static library by itself.** A `PublishAot` project
  that references a `StaticLibrary` asm project needs nothing more: `dotnet publish -r <rid>`
  builds the library for that RID, adds the `DirectPInvoke` item (its `AssemblyName`, the name
  `[LibraryImport]` uses) and the `NativeLibrary` item (its `.lib` or `.a`, from its
  `GetAsmLinkInputs`), and the code is linked into the executable. 0.3.0's consumer targets
  dropped static libraries, and the interop guide showed both items written by hand, per OS.
  Only for `PublishAot` with a RID: a JIT app still cannot load a static library. Tested
  (tests/projects/NetAotStatic) on each CI machine: the published executable runs and nothing
  is published beside it. The manual QA's F7, the owner's choice (2026-11-23).
- **`OptionalOnUnlistedRuntimes`, an asm library that an app can do without.** With
  `<OptionalOnUnlistedRuntimes>true</OptionalOnUnlistedRuntimes>` in an asm project, a .NET
  build for a RID that none of its RIDs serves goes on without it, with the warning
  **ASM0049**, instead of stopping with ASM0040: nothing is built, copied, listed in
  `deps.json` or linked, and the app's code must cope with `DllNotFoundException`. The default
  stays the error, whose message now names the property. A portable build is unaffected. The
  manual QA's F2, the owner's choice (2026-11-23).

### Changed

- **A shared library's references to static libraries are private by default.** The SDK sets
  `PrivateAssets="all"` on a shared library's `ProjectReference` to a `StaticLibrary` project
  that sets no `PrivateAssets` (its own evaluation says what it is), before NuGet reads the
  references for restore (`_AsmMakeStaticLibraryReferencesPrivate`), as a program's references
  already were. The static library's code is linked in, so the shared library's package no
  longer depends on it (its consumers failed with `NU1101` when the static library had no
  package) and its referencing projects no longer get it as a transitive reference.
  `PrivateAssets="none"` keeps the dependency. Not in a shared library with
  `AsmProduceStaticLibrary`, whose archive's users need the static libraries it links. A restore
  that does not run MSBuild's restore targets does not see it; write `PrivateAssets="all"` for
  such a tool. The delivery QA's open item, the owner's choice (2026-11-23).
- **`asmsdk.inc`'s function macros start with NASM's `__SECT__`,** the section the source last
  named with a `section` line (`[section .text]` while it named none), so a source that
  includes `asmsdk.inc` and names no section links on Windows with `ImplicitUsings` off too (see
  Fixed: NASM's COFF output writes a label before the first section directive as undefined).
  One change of meaning: after the bracket form `[section .foo]`, which does not redefine
  `__SECT__`, a function goes back to the last `section` line's section. Code sections written
  with `section .foo` are unaffected. The owner's choice (2026-11-23).
- **A GNU-syntax source's generated wrapper holds the source's text behind a line marker**
  (`# 1 "<full path>"`), no longer an `.include` of it. llvm-mc's `-g` writes its MAIN file into
  every DWARF line row, with the line numbers of whatever buffer the instruction came from. It
  also gives an included file's last statement the including file's line (measured with
  llvm-dwarfdump on wasm32, AArch64 and x86-64). So Debug builds of ImplicitUsings sources
  pointed debuggers at `obj/.../implicit/x.s`. Now every row names the source and its own
  line. Errors are still reported at the source's full path, line and column. Release objects
  carry no trace of the marker (measured). The wrapper target is `_AsmWriteSourceWrappers`
  (the `AsmWriteSourceWrappers` task), which also writes WebAssembly's define labels.
- **A file named `x.wasm.s`, `x.browser.s` or `x.wasi.s` is a WebAssembly source** (the new
  qualifiers). Before, it was an unqualified ARM64 file named `x.wasm`.
- **`NativeLibrary` replaces `AsmLinkLibrary`** (no compatibility, the owner's rule).
  `NativeLibrary` is NativeAOT's item for extra link inputs, as `LinkerArg` already was for
  linker arguments. Packages now write `NativeLibrary` items in their props.
  - **`ASM0109`, an error, catches both cases.** An `AsmLinkLibrary` item in a project gets the
    replacement line. One added by a package packed with 0.3.0 gets the package's name, to be
    packed again. `ASM0108` then stops the build, as for the other renamed settings.
- **The sources are `Compile` items and the include files `None` items,** .NET's own item types,
  in place of `AsmSource` and `AsmInclude` (the owner's rule: "We discard unnecessary *Asm* in
  favor of preexisting in the ecosystem"). `<Compile Remove="old/**" />` and
  `<Compile Include="../shared/x.asm" />` work as in a `.csproj`, and `.NET`'s
  `EnableDefaultCompileItems` replaces `EnableDefaultAsmItems`. Every `None` item ending in
  `.inc` is a dependency of every source, and any other file a source reads is a
  `CustomAdditionalCompileInputs` item, .NET's item for more inputs of the compile. IDEs show the
  files with their usual Build Action, and no `AvailableItemName` is declared. Measured before
  the switch: Rider's engine (ReSharper InspectCode 2026.3) never analyzes a `.asm` or `.s`
  `Compile` item as C#, though it does a `.cs` one in the same project, and Visual Studio's C#
  language service needs the `CSharp` and `Managed` capabilities, which an `.asmproj` lacks
  (a test checks they stay absent).
  - **`ASM0110` to `ASM0112`, errors, catch the old names** (`AsmSource`, `AsmInclude`,
    `EnableDefaultAsmItems`) and show the new line; `ASM0108` then stops the build.
  - **`ASM0025`:** a `Compile` item that is not `.asm` or `.s`. NASM would otherwise read a
    `.c` file as NASM.
- **NASM's messages name a source by its path relative to the project** (`add.asm(3): error :
  …`), the path it now gets. IDEs open it from the project folder.
- **The test suite's tool-run pattern sees NASM again.** It matched `nasm -f`, but `-Xvc` comes
  first. Since then, the incremental-build and design-time tests had checked only the linker and
  the archiver.

### Fixed

- **`dotnet publish -o <folder>` and `dotnet build -o <folder>` of an asm project without `-r`
  write each RID to `<folder>/<rid>/` with every .NET SDK.** The .NET SDK 10.0.404 CLI passes the
  folder without a trailing separator (11.0.100 adds one), as a global property the common
  targets cannot normalize, and the RID outer build appended the RID straight to it:
  `outlinux-x64/` next to `out/` (measured: ProgramTests' publish test failed under 10.0.404,
  which the CI's WebAssembly runner now uses). The folders are made directories first, and the
  "OutDir set by hand" check compares them as such.
- **Objects keep their source's subfolder.** `src/x.s` assembles to `obj/.../asm/obj/src/x.o`,
  as the targets always meant, so `a/x.asm` and `b/x.asm` no longer write one object over the
  other. `RecursiveDir` is MSBuild's own metadata, which the items a task returns lack:
  `AsmSelectSources`' output had it empty, and every object went to `obj/x.o` (measured with a
  source in `src/`). The folder now travels as `AsmRecursiveDir`.
- **A NASM source that names no section links on Windows.** `ImplicitUsings` now ends with
  `section .text` on every OS, not only after the Linux note. NASM's COFF output writes a label
  defined before the first section directive as an undefined symbol, although its code goes to
  `.text` (measured with the official NASM 2.16.03, 3.01 and 3.02; ELF and Mach-O are not
  affected). So the `asmstatic` template's `mul_ints`, any `HIDDEN_FUNCTION`, program `main` or
  `FUNCTION` of a project with an `ExportsFile` in a source without a section directive, and a
  `LOCAL_FUNCTION` that another section points to, assembled (and archived) silently and then
  failed to link on `win-x64` (`undefined symbol: main`). A `FUNCTION` in any other DLL escaped
  only because its export directive switches back with `__SECT__`.
  The PlugAndPlay scenario found it when its program first linked the template's library; the
  Sectionless test projects now link section-less sources for all eight RIDs. The Windows
  object of a data-only source now carries an empty `.text` section, which changes nothing.
- **A `DefineConstants` given on the command line keeps the SDK's own symbols.**
  `-p:DefineConstants=X` (or a CI script's global property) is read-only, so MSBuild silently
  ignored the SDK's append of `DEBUG`/`RELEASE`, `FREESTANDING`/`LIBC` and the platform's
  symbols, and the assemblers got `X` alone. NASM's `asmsdk.inc` recognizes the platform by
  itself, but `asmsdk-gnu.inc` took the ELF branch on Windows and macOS: CI on Windows on Arm
  failed with `expected absolute expression` at `.type`. The symbols are now added to whatever
  `DefineConstants` holds. (C# loses `DEBUG` and `TRACE` the same way; for assembly it broke the
  platform layer.)
- **`Update` and `Remove` of default items work in an `.asmproj`.** The default `AsmSource`,
  `AsmInclude` and `None` globs were declared after the project body, so
  `<None Update="data.txt" CopyToOutputDirectory="PreserveNewest" />` matched nothing and was
  silently ignored. They are now declared in `Sdk.props`, before the body, where .NET declares
  its own. `EnableDefaultAsmItems` and `EnableDefaultNoneItems` set in the body still turn them
  off: MSBuild evaluates every property before any item.
- **`CopyToOutputDirectory` and `CopyToPublishDirectory` work in an `.asmproj`.** The SDK's
  `CopyFilesToOutputDirectory` copied nothing but printed the output's path. Marked files now
  reach every RID's output folder and publish folder, at their `TargetPath`.

## [0.3.0] — 2026-11-21

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
- **`BinariesCopyPath`: where a .NET project keeps the asm libraries,** for build and publish:
  on a `ProjectReference` to an `.asmproj`, on a `PackageReference` to an asm package, or as
  the project's property. `Runtimes` (`runtimes/<rid>/native/`), `AppFolder`, `RidFolders`
  (`<rid>/`), `Auto` (the default: NuGet's layout, `runtimes/` portable and the app folder for
  one RID), or any folder inside the output with an optional `{rid}` (`native/{rid}`). The
  reference wins, then the property, then the choice of a .NET library the asm library comes
  through (so the attribute on a wrapper library's reference shapes the app's output too),
  then `Auto`. Errors `ASM0043` (a value that leaves the output or is malformed) and
  `ASM0044` (two files on one path: a folder without `{rid}` in a build of several RIDs of one
  OS); warning `ASM0046` (two libraries in between disagree).
- **The app finds them wherever they go,** with no code of its own: its `deps.json` lists every
  asm library with `localPath`, as `runtimeTargets` in a portable app and as plain `native`
  assets in an app for one RID. Measured with .NET 10 on Windows and Linux: the host loads from
  any folder so named, framework-dependent and self-contained; a self-contained app ignores
  `runtimeTargets`, and without `localPath` the host looks only beside the app.
- **A generated fallback loader** for what reads no `deps.json`: a C# project for .NET 5 or
  later that puts an asm library anywhere but the app folder gets
  `obj/.../AsmNativeLibraryLoader.g.cs`, an `AssemblyLoadContext.ResolvingUnmanagedDll`
  handler that runs only after .NET's own probing and any resolver of yours failed. It is how
  a NativeAOT program finds `native/x.dll`, and how an app finds a library that reaches it
  through a class library whose asm reference does not flow (`PrivateAssets="all"`).
  `AsmGenerateNativeLibraryLoader=false` turns it off.
- **`PrivateAssets="all"` packs the asm binaries into a .NET library's package:** every RID's
  library under `runtimes/<rid>/native/`, with no dependency on the asm package; without it,
  the reference stays a package dependency, as NuGet makes any `ProjectReference`. Within the
  solution, an app that references such a library by project gets neither the asm reference
  nor AsmSdk's targets, and copies the files where the library's `BinariesCopyPath` put them:
  a .NET project hands its asm references' files on already laid out (they are marked
  `Private="false"` and delivered through its own copy and publish lists, with
  `ExcludeFromSingleFile` for single-file publishes), because what
  `GetCopyToOutputDirectoryItems` hands on is fixed before any later target could move it.
- **Packages with asm libraries carry .NET consumer targets** (`buildTransitive/<tfm>/<id>.targets`,
  plain MSBuild, named for the package only), which give their .NET consumers
  `BinariesCopyPath` without depending on AsmFoundation.Sdk (an asm package still does not:
  NU1605 for consumers on an older SDK). They move NuGet's own items; the .NET SDK then writes
  the new `localPath` into `deps.json` itself.
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
  ARM64, run natively on each of the five CI machines, and again with `BinariesCopyPath`
  `native/{rid}` and `RidFolders`), `NativeLayoutTests` (every `BinariesCopyPath` value and
  where it comes from, builds for one RID, self-contained, single-file and NativeAOT
  publishes that run, `ASM0043` and `ASM0044`, the generated loader, a private reference
  through a class library in build, single-file publish and pack, and the .NET consumers of
  both package kinds), and `ExampleTests` for every fact the documentation states about the
  samples. AsmSdk.Tests has 165 tests.
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
