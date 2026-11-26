# `.asmproj` reference

Everything AsmFoundation.Sdk reads from a project, sets, runs and writes, with the exact
defaults of the 0.3.0 targets. The guides explain when to use each setting. Start at
[What is AsmSdk](guide/index.md).

- **Properties** go in a `<PropertyGroup>`, and **items** in an `<ItemGroup>`, as in any
  SDK-style project.
- **A setting that has a .NET name uses it:** `RuntimeIdentifiers`, `RuntimeIdentifier`,
  `TargetFramework`, `OutputType`, `AssemblyName`, `DefineConstants`, `DebugSymbols`,
  `DebugType`, `StartupObject`, and NativeAOT's `LinkerArg` and `ExportsFile`. `Asm*` names
  exist only where .NET has no counterpart: the toolchain, packing, and the assembler and
  linker details.
- **Anything not listed works as it does for a `.csproj`,** for example `Version`,
  `PackageId`, `Configuration`, `Directory.Build.props`, `Directory.Packages.props`,
  `nuget.config` and `global.json`.


## How a project is evaluated

MSBuild reads a project in this order, and the order explains most surprises:

1. **`Sdk/Sdk.props`** (AsmFoundation.Sdk) sets `UsingAsmFoundationSdk=true` and switches
   off the managed build. It then imports `Microsoft.NET.Sdk`'s `Sdk.props`, which reads
   `Directory.Build.props`. So `$(UsingAsmFoundationSdk)` is already `true` inside
   `Directory.Build.props`, and `$(TargetFramework)` is still empty there, as in any .NET
   project.
2. **The project body.** Your properties and items.
3. **`Sdk/Sdk.targets`.** It imports these files, in this order:
   1. `Sdk/AsmFoundation.Sdk.Version.props`, generated at pack time:
      `AsmFoundationSdkVersion=0.4.0` and `AsmFoundationDefaultToolchainVersion=0.2.0`.
   2. `targets/AsmFoundation.Properties.targets`. **Every SDK default is applied here,
      after your project body.** A default applies only when you left the property empty.
      This file also maps the target framework to a NuGet framework, decomposes the RID, and
      hides `OutputType`, `DebugType`, `DebugSymbols` and `DisableImplicitFrameworkDefines`
      from the managed SDK (see [Properties the SDK sets for you](#properties-the-sdk-sets-for-you)).
   3. `Microsoft.NET.Sdk`'s `Sdk.targets`: restore, pack, references and Build.
   4. `targets/AsmFoundation.Build.targets`, `targets/AsmFoundation.References.targets` and
      `targets/AsmFoundation.Pack.targets`. These replace managed targets of the same name.

**Consequences:**

- A project that reads an SDK default sees it empty. `$(DefineConstants)` inside your project
  does not contain `DEBUG` yet: like C#, the SDK appends `DEBUG` (or `RELEASE`), the target
  framework symbol and the platform symbols after the body. So `<DefineConstants>FOO</DefineConstants>`
  keeps them, and so does `<DefineConstants>$(DefineConstants);FOO</DefineConstants>`.
- After `Properties.targets`, `OutputType` is always `Library`, `DebugType` is `none` and
  `DebugSymbols` is `false`, whatever you set: the SDK has read your values into internal
  properties first. A target of your own that runs later sees the hidden values.
- `$(RuntimeIdentifier)` is set in each inner build (one per RID), so a condition on it works
  in the project body, as in a `.csproj`.

## Runtime identifiers

| RID | Format and linker | Shared library | Static library | Executable |
|---|---|---|---|---|
| `win-x64`, `win-arm64` | PE/COFF, lld-link | `<name>.dll` + `<name>.dll.lib` | `<name>.lib` | `<name>.exe` |
| `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64` | ELF, ld.lld | `lib<name>.so` | `lib<name>.a` | `<name>` |
| `osx-x64`, `osx-arm64` | Mach-O, ld64.lld | `lib<name>.dylib` | `lib<name>.a` | `<name>` (`libc` only) |
| `browser-wasm`, `wasi-wasm` | WebAssembly, wasm-ld | `<name>.wasm` + `<name>.a` | `<name>.a` | `<name>.wasm`, a WASI command (`wasi-wasm` only) |

| Property | Default | Meaning and footguns |
|---|---|---|
| `RuntimeIdentifiers` | the build machine's RID | the RIDs the code supports, semicolon-separated. `dotnet build` builds each one in its own inner build. A project that lists none is built for what its referencing projects ask for, or for `-r`, and otherwise for the build machine's own RID (`win-x64` on Windows x64): `ASM0013` warns a `dotnet pack`, or a portable publish or pack of a referencing .NET project, that carries that one RID, and `ASM0010` stops the build on a machine that is none of the eight. Restore always lists all ten, `browser-wasm` and `wasi-wasm` included, so the SDK overwrites the property after reading your list. |
| `RuntimeIdentifier` | none | one RID: what an inner build is for (`-r`, or a referencing project). In the project, a list of one. A RID the project's list does not have is `ASM0012`, and a RID outside the ten `ASM0011`. |
| `AsmRequestedRuntimeIdentifiers` | none | a global property the consumer targets set: the RIDs a `ProjectReference`'s `RuntimeIdentifiers` metadata asks for, comma-separated. They replace the project's list in its RID outer build. |
| `IsRidAgnostic` | `false` (set by the SDK) | tells referencing projects to pass their `RuntimeIdentifier` on, so a RID-specific build of a referencing project builds this project for its RID. |

- **Inner and outer builds.** With a `RuntimeIdentifier`, a build assembles and links that RID.
  Without one, it is the *RID outer build*: it builds each RID (in parallel when the build is)
  and links nothing itself, as the outer build of a multi-targeted project builds each
  framework. A solution build of a project that lists no RID builds the build machine's RID,
  like any build of the project; on a machine that is none of the eight, it builds nothing for
  it by itself, with a message, instead of `ASM0010`.
- **Sources per RID.** Every `.asm` or `.s` `Compile` item competes per RID by file name: `.asm`
  is NASM for the x64 RIDs, `.s` is GNU syntax for llvm-mc (the ARM64 RIDs, and WebAssembly's
  when qualified `wasm`, `browser` or `wasi`), and a qualifier before the extension (`x.win.asm`,
  `x.linux-musl.asm`, `x.unix.s`, `x.arm64.s`) narrows a file. The most specific file of a
  name wins. See [Platforms](guide/platforms.md#sources-per-platform), and `ASM0020` to
  `ASM0024` below.

## Target frameworks

The target framework says what the code links against, the way `net10.0` says which .NET it
runs on.

| `TargetFramework` | The code | Executable | Shared library | Runs on |
|---|---|---|---|---|
| `freestanding` (the default) | links no C library: Linux system calls; kernel32 on Windows | Linux: static, entered at `_start`. Windows: entered at `_start`. macOS: none (`ASM0015`) | needs nothing (kernel32 on Windows) | any Linux kernel of its CPU; any Windows |
| `libc` | links the platform's C library | Linux: PIE, entered at `main` through the SDK's `crt1`. Windows: entered at `main` through the SDK's `mainCRTStartup`. macOS: dyld calls `main` | `DT_NEEDED` on the C library; an import of `ucrtbase.dll`; a load command for libSystem | glibc 2.34 or later, musl, Windows 10 or later, macOS 11 or later |

- **`TargetFrameworks`** builds the project once per name, exactly like a multi-targeted
  `.csproj`: `<TargetFrameworks>freestanding;libc</TargetFrameworks>`. Every build has its
  own `bin/<Configuration>/<tfm>/<rid>/` and `obj/<Configuration>/<tfm>/<rid>/` folders. A
  framework may list RIDs of its own: `<RuntimeIdentifiers Condition="'$(TargetFramework)' == 'libc'">…`.
- **No `TargetFramework`** means `freestanding`. The default is applied after the project
  body, so a project's own `TargetFrameworks` still multi-targets.
- **Any other name fails** with `ASM0003`, including `netstandard2.0`, which was the hidden
  default of 0.1.0.
- **Preprocessor symbol:** the assembler gets `FREESTANDING` or `LIBC`, the counterpart of a
  .csproj's `NET10_0`, so one source can serve both with `%ifdef LIBC`.
  `DisableImplicitFrameworkDefines=true` turns it off, as in .NET.
- **Names are case-insensitive** in conditions, and the folders are lower case.

**Behind the names.** NuGet only knows its own frameworks, so each name is an alias:

| Name | NuGet framework | Package folders |
|---|---|---|
| `freestanding` | `netstandard1.0` | `lib/netstandard1.0/`, `build/netstandard1.0/` |
| `libc` | `netstandard2.0` | `lib/netstandard2.0/`, `build/netstandard2.0/` |

The mapping makes NuGet's compatibility rules match the linker's:

- **A `libc` project may reference `freestanding` projects and packages:** freestanding code
  links into a libc program.
- **A `freestanding` project that references a `libc`-only project fails at restore with
  `NU1201`**, and a `libc`-only package with `NU1202`: a freestanding program cannot link
  code that calls the C library.
- **A project that targets both is referenced by its nearest framework.** A `libc` project
  gets the `libc` build, and a `freestanding` project gets the `freestanding` build.
- **Any `.csproj` can reference any `.asmproj`,** because every .NET implementation accepts
  both frameworks. A .NET project gets the `libc` build of a project that targets both.
- **The two names need two frameworks.** With one framework behind both, `dotnet pack` fails
  with `NU5051` and a `.csproj` reference with `NU1201` (measured with .NET SDK 11.0.100).

## Output properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `OutputType` | `Library` | `Library` gives a shared library, `StaticLibrary` a static one, `Exe` or `WinExe` an executable (a console program), and `Objects` object files only. Any other value builds a shared library. The SDK reads it into its internal `_AsmOutputKind` (`SharedLibrary`, `StaticLibrary`, `Executable` or `Objects`) and then **sets `OutputType` back to `Library`**, so the managed SDK never builds an apphost or a runtimeconfig. A target of your own can read `_AsmOutputKind`, but it is internal and may change. |
| `AssemblyName` | the project name | the base name of every output (see the table above). Libraries get the `lib` prefix on Linux and macOS, because `-l<name>` and .NET's `[DllImport("<name>")]` both look for it there. The packed static library is named after it too, whatever the `PackageId`. |
| `AsmProduceStaticLibrary` | `false` (`true` for a `Library` built for `browser-wasm` or `wasi-wasm`) | also archive the static library next to a shared one. A referencing project then links the static one. On WebAssembly the archive is what .NET apps and other asm projects link (a module cannot be linked against), and it holds the static libraries and object files the library links, as the module does; `false` there ships the module alone, and hands over the objects instead. |
| `OptionalOnUnlistedRuntimes` | `false` | `true`: a .NET project built for a RID this library does not build (none of its RIDs serves it) goes on without it, with the warning `ASM0049`, instead of stopping with `ASM0040`. For an app whose code checks for the library and copes with `DllNotFoundException`. A portable .NET build is unaffected. |
| `TargetName` | `lib$(AssemblyName)` for libraries on Linux and macOS, `$(AssemblyName)` otherwise | set only when empty. Prefer `AssemblyName`. |
| `TargetFileName` | `$(TargetName)` plus `.dll`, `.so`, `.dylib`, `.lib`, `.a`, `.exe` or nothing | set explicitly per RID, so that the common targets never append `.dll` on their own. |

Outputs go to `bin/<Configuration>/<TargetFramework>/<rid>/`, as in .NET
(`AppendTargetFrameworkToOutputPath` and `AppendRuntimeIdentifierToOutputPath` keep their
defaults, `true`). Each inner build prints `<Project> -> <output>`, as a `.csproj` does.

**`dotnet build -o <folder>`** sets a global `OutputPath`, which the .NET SDK never extends by
the RID. The RID outer build therefore gives each RID `<folder>/<rid>/`
(`<folder>/<tfm>/<rid>/` in a project with both frameworks), and an explicit `OutDir` the
same; one folder for all of them would let each RID overwrite another's files. The projects an
inner build references get the same folder, as any global property reaches them. A .NET
project built with `-o` does not pass its folder on: its asm references build in their own
`bin/`, and the app's folder gets the copies.

## Runtime and link properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `TargetFramework` | `freestanding` | see [Target frameworks](#target-frameworks). `libc` links the platform's C library, through a stand-in the SDK writes. A `StaticLibrary` or `Objects` project links nothing, but its target framework still decides who may reference it. |
| `StartupObject` | empty | the executable's entry symbol, .NET's "where the program starts". Empty means `_start` for a `freestanding` program, and the SDK's startup code for a `libc` one (Linux `crt1`: `_start` → `__libc_start_main` → `main`; Windows `mainCRTStartup` → `main`). Setting it on `libc` skips that code (Linux, Windows), and your entry must then do its work. On macOS it names the function dyld calls instead of `main`. |
| `AsmDynamicLinker` | `/lib64/ld-linux-x86-64.so.2` (linux-x64), `/lib/ld-linux-aarch64.so.1` (linux-arm64), `/lib/ld-musl-x86_64.so.1` (linux-musl-x64) | `PT_INTERP` of a Linux `libc` executable. |
| `AsmMacOSMinimumVersion` | `11.0` | the macOS version Mach-O records (`-platform_version`, and llvm-mc's triple). |
| `ExportsFile` | empty | NativeAOT's name for the linker version script, a path relative to the project folder. Linux: `--version-script=<file>`; only the names it lists as global stay exported, versioned by its node; an executable also gets `--export-dynamic`, as NativeAOT does. Windows: `-export:` for each global name, and `FUNCTION` writes no directive of its own (`ASMSDK_EXPORTS_FILE`); an executable then also gets its import library. macOS: `-exported_symbol _<name>` for each name, patterns kept. `extern "C++"` blocks, or patterns on Windows, are `ASM0030`. Editing the file relinks. |
| `AsmSharedLibraryPath` | `$(TargetPath)` for a shared library | computed in an inner build. Overwritten even when you set it. |
| `AsmImportLibraryPath` | `$(TargetDir)$(AssemblyName).dll.lib` for a Windows DLL | the import library lld-link writes beside the DLL; what a referencing asm project links. |
| `AsmStaticLibraryPath` | `$(TargetPath)` for a `StaticLibrary`; the static library's name beside the shared one with `AsmProduceStaticLibrary` | where the archive goes. Set only when empty. |

Extra linker arguments are `LinkerArg` items (see [Items](#items-and-their-metadata)).
[Runtimes and linking](guide/runtimes-and-linking.md#the-exact-link-lines) has the exact link
line of each output on each OS, and
[The C library stand-ins](guide/runtimes-and-linking.md#the-c-library-stand-ins) what the SDK
writes for `libc`.

**After each Windows or macOS link**, the SDK checks the image for load-time fixups its loader
cannot apply, which lld-link and ld64.lld accept: `ASM0080` for a 32-bit absolute address in a
64-bit Windows image, `ASM0081` for a fixup in a read-only Mach-O segment. A failed check
deletes the image, so the next build links and checks again.

## Assembly properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `EnableDefaultCompileItems` | `true` (.NET's own switch) | adds `**/*.asm` and `**/*.s` as `Compile` items, excluding `bin/`, `obj/` and the other `DefaultItemExcludes`. Set `false` to list them yourself. 0.3.0's `EnableDefaultAsmItems` is `ASM0112`. |
| `EnableDefaultNoneItems` | `true` (.NET's own switch) | adds every other file of the project folder as a `None` item, as a `.csproj` does, so IDEs show it: an include file, a linker script, a C header, a README. The sources are taken out of `None`, so each file is one item. Set `false` to list `None` items yourself; the `.inc` ones among them are the sources' dependencies. |
| `DefineConstants` | empty, then the SDK appends its symbols | semicolon-separated `NAME` or `NAME=VALUE`: `-DNAME` for NASM, `--defsym NAME=1` (or `=VALUE`, integers only) for llvm-mc. After your project body the SDK appends `DEBUG` or `RELEASE` (the configuration name in upper case, with `-`, `.` and spaces as `_`, exactly like C#), `FREESTANDING` or `LIBC`, `WINDOWS`, `LINUX` or `MACOS` (plus `MUSL`), and `X64` or `ARM64` (WebAssembly: `BROWSER` or `WASI`, and `WASM`, as labels), so setting the property never drops them. `DisableImplicitConfigurationDefines=true` turns the first off, and `DisableImplicitFrameworkDefines=true` the others. C#'s `TRACE` is not added. The SDK also defines `ASMSDK_EXPORTS_FILE` with an `ExportsFile`, and `ASMSDK_EXECUTABLE` in a program. |
| `DebugSymbols` | `true` in Debug (from `Microsoft.NET.Sdk.props`), `false` otherwise | debug information: DWARF (`-g -F dwarf`, llvm-mc `-g`) on Linux and macOS; CodeView (`-g -F cv8`) and a PDB on Windows. Set `true` for it in Release. Unlike .NET, whose Release builds still write a PDB, a Release build stays free of debug information by default. |
| `DebugType` | `portable` (from `Microsoft.NET.Sdk.props`) | `none` turns debug information off even when `DebugSymbols` is `true`. Other values change nothing. |
| `AsmFlags` | empty | extra NASM arguments. They are placed before `-I` and `-D`. Recommended: `-Xvc`, which makes NASM messages MSBuild diagnostics. |
| `AsmLlvmMcFlags` | empty | extra llvm-mc arguments, for `.s` sources. |
| `TreatWarningsAsErrors` | `false` | adds `-w+error` to NASM. NASM 3 warns `implicit DEFAULT ABS is deprecated` on any memory operand in a file without `default rel` or `default abs`. With this property, that warning fails the build. |
| `NoWarn`, `WarningsAsErrors`, `WarningsNotAsErrors` | .NET's | each entry shaped like a NASM warning name (lower case, hyphens: `label-orphan`) becomes `-w-<name>`, `-w+error=<name>` or `-w-error=<name>`, after `-w+error`, so it wins; other entries (`NU5128`, `CS1591`, `1701`) are left to the tools they belong to. NASM ignores a name it does not know. llvm-mc has no per-warning switches. MSBuild's own warnings (`ASM0013`) are silenced by `NoWarn` as any MSBuild warning is. |
| `ImplicitUsings` | `enable` | `enable` or `true`: the platform layer in front of every source. NASM gets `-P include/asmsdk-implicit.inc` (`asmsdk.inc`, `default rel`, on Linux `.note.GNU-stack`, then `section .text` on every OS, without which NASM's COFF output leaves a label before the first section directive undefined); each `.s` is assembled through `obj/.../asm/implicit/<path>`: `.include "asmsdk-gnu-implicit.inc"`, then the source's own text behind a line marker naming the source (so diagnostics and DWARF stay on it), since llvm-mc has no pre-include; a WebAssembly source's wrapper includes `asmsdk-wasm.inc` instead. `disable` (or anything else) gives raw NASM. Code that writes the includes itself is unaffected: both headers have guards. In a `.asmproj` the .NET meaning (C# global usings) does not apply. |
| `AsmIntermediateDirectory` | `$(IntermediateOutputPath)asm/` (`obj/<Configuration>/<tfm>/<rid>/asm/`) | objects in `obj/`, the system library stand-ins and startup code in `stubs/`, generated package files in `pack/`. |

**The NASM warning set is fixed:** `-w+all -w-reloc-rel-dword -w-reloc-abs-qword`.

- The two exceptions fire on relocations that NASM cannot judge. The first is a RIP-relative
  reference to another section, which position-independent code needs and which always
  links. The second is a `dq label`: valid in a writable section, or anywhere in a Linux
  freestanding executable, and rejected by the linker (or the SDK's image check) in
  read-only data of any other output.
- Add `-w-<name>` to `AsmFlags` to silence another warning, for example
  `-w-label-orphan`. The name is printed in brackets after each warning.
- The SDK's own stubs are always assembled with `-w+error`.

**The assembler command lines, as run** (from the project folder, with forward-slash paths):

```text
nasm -Xvc -f win64|elf64|macho64 [--gprefix _] [-D.rodata=.rdata] [-g -F dwarf|cv8] -w+all -w-reloc-rel-dword -w-reloc-abs-qword [-w+error] $(AsmFlags)
     -I"<dir>/" ... -D<your symbols> ... -DDEBUG|-DRELEASE -DFREESTANDING|-DLIBC -DWINDOWS|-DLINUX|-DMACOS -DX64
     -o "<obj>/x.o" "<project>/x.asm"

llvm-mc --triple=<cpu>-<os> --filetype=obj [-g] $(AsmLlvmMcFlags) -I "<dir>/" ... --defsym=<symbol>=1 ...
        -o "<obj>/x.o" "<project>/x.s"
```

`-Xvc` prints NASM's messages as `x.asm(12): error : …`, the form MSBuild recognizes, so each is an
MSBuild error or warning with its file and line (NASM's own `x.asm:12:` form has no column, and
MSBuild left it as plain output). `--gprefix _` is macOS's (Mach-O's underscore), and
`-D.rodata=.rdata` Windows' (NASM's win64 output makes an unknown section name executable code). [The tools](toolchain/tools.md) has
the triples.

## Sharing and pack properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmGenerateCHeader` | `true` | the C header `<AssemblyName>.h` from the `PROTO` and `STRUCT` lines, written beside each API file and packed under `include/` (see [C and other languages](guide/interop.md#c-and-other-languages)): the functions' declarations, the structs and checks of their layout. `false`: none. A project's own `include/<AssemblyName>.h` wins (`ASM0149` at pack). |
| `AsmPackSources` | `false` | also pack the `.asm` and `.s` `Compile` items and the `None` items ending in `.inc` into the package's `src/`, keeping their relative paths. |
| `AsmPackageLinkKind` | `Static` | what a consuming `.asmproj` links: `Static` takes the package's static library when it has one, otherwise its shared one (on Windows, the DLL's import library). `Shared` takes the shared one when it has one, otherwise the static one. |
| `DisableAsmConsumerTargets` | `false` | `true` drops the implicit `PackageReference` to `AsmFoundation.Sdk` that brings the consumer targets to referencing .NET projects. They then get no `runtimes/` layout and no `deps.json` entries. |
| `PackageId`, `Version`, `Authors`, `Description`, `PackageLicenseExpression`, `PackageReadmeFile`, … | as for any NuGet package | standard NuGet metadata. `NU5128` is suppressed on purpose. |

**Package layout.** `<tfm>` is the target framework, `<nuget-tfm>` the NuGet framework behind
it, and `<rid>` each RID the project builds. `dotnet pack` builds them all:

| Path | What |
|---|---|
| `include/**` | the `AsmPublicInclude` files, once, whatever the number of target frameworks; and `include/<AssemblyName>.h`, the C header the SDK writes from the `PROTO` and `STRUCT` lines (`AsmGenerateCHeader`), unless the project ships its own |
| `contentFiles/cs/net7.0/AsmBindings/<AssemblyName>.cs.pp`, `build/bindings/<AssemblyName>.api.json`, `build/bindings/AsmFoundation.Sdk.Bindings.dll` | the C# bindings of a library with `PROTO` or `STRUCT` lines: the baseline NuGet compiles into each C# consumer for .NET 7 or later, and the API file and generator for a consumer whose code names `[AsmLibrary]` (see [Generated bindings](guide/interop.md#generated-bindings)) |
| `src/**` | the sources, with `AsmPackSources` |
| `lib/<nuget-tfm>/_._` | an empty marker per target framework. NuGet judges compatibility by `lib/`: without it, a package with no `lib/` counts as compatible with every framework, and a freestanding project would restore a libc-only package, import nothing and fail at link time. The marker adds no assembly reference to a .NET consumer. |
| `build/<nuget-tfm>/<id>.targets`, `buildTransitive/<nuget-tfm>/<id>.targets` | generated per target framework for the package's .NET consumers, in plain MSBuild: `BinariesCopyPath` and the fallback loader (a shared library), and the C# bindings' targets (`PROTO` or `STRUCT` lines). Every name is suffixed with the package id, except the bindings' three shared targets. |
| `build/<nuget-tfm>/<id>.props`, `buildTransitive/<nuget-tfm>/<id>.props` | generated per target framework: add the include folder and the library of the consuming `.asmproj`'s RID, by the RID graph's fallbacks, or stop with `ASM0060` when the package has none. NuGet imports the one nearest to the consumer's framework. |
| `native/<rid>/<tfm>/` | the static library (`lib<name>.a`, `<name>.lib`), a Windows DLL's import library (`<name>.dll.lib`), and a WebAssembly library's module (`<name>.wasm`) and archive (`<name>.a`): for asm consumers, and for .NET WebAssembly apps, which link the archive |
| `runtimes/<rid>/native/` | the shared library of a single-target project (`<name>.dll`, `lib<name>.so`, `lib<name>.dylib`). .NET consumers get it copied and found by `[DllImport]`. |
| `runtimes/<rid>/nativeassets/<nuget-tfm>/` | the shared library of each target framework of a multi-targeted project (the file names would collide in `native/`). NuGet gives a .NET consumer the nearest framework's, which is `libc`'s. |

The implicit `AsmFoundation.Sdk` dependency is removed from the `.nuspec`: a package's
consumers need nothing from the SDK.

## Program properties

See [Programs](guide/programs.md).

| Property | Default | Meaning and footguns |
|---|---|---|
| `BinariesCopyPath` | `Auto`, which for a program is its own folder | where a program keeps the asm shared libraries and programs it references, as in a .NET project. The reference's `BinariesCopyPath` metadata wins. The program's run path follows each library folder (`$ORIGIN/<folder>`, `@loader_path/<folder>`). On Windows a library folder other than the program's own is `ASM0047`. |
| `AsmRunIn` | `Auto` | how `dotnet run` runs the program: `Auto` (this machine's RID, another it runs natively, one it emulates, then WSL, then Docker), `Host`, `Wsl` or `Docker`, or `Wasi`: the `wasi-wasm` build in wasmtime (from `PATH`), which `Auto` tries after the machine's own builds. |
| `AsmRunWslDistribution` | WSL's default | the WSL distribution `dotnet run` uses. |
| `AsmRunDockerImage` | `debian:stable-slim` (glibc), `alpine:3` (musl) | the image `dotnet run` uses for a Linux build. The program's folder is mounted at `/app`, its working directory. |
| `RunArguments`, `RunCommand` | empty | as in .NET: the program's default arguments, and a command of your own that `dotnet run` starts instead. |
| `PublishRelease` | `true` | `dotnet publish` builds Release unless `-c` says otherwise, as for a .NET 8 project. |
| `PackAsTool` | `false` | `true`: `dotnet pack` writes a .NET tool: `<PackageId>` (`DotnetTool`, `tools/any/any/`) and `<PackageId>.<rid>` per RID (`DotnetToolRidPackage`, `tools/any/<rid>/`, `Runner="executable"`). The managed SDK never sees it (its own tool targets refuse a project that is not .NETCoreApp). `ASM0053` for anything but a program with one target framework. |
| `ToolCommandName` | the program's name | the command a `PackAsTool` package installs. |

**A program's package** (`dotnet pack`, without `PackAsTool`): `runtimes/<rid>/native/` holds
each RID's program and the libraries it links next to it (a folder of their own would be
`ASM0051`: NuGet copies these files flat into an app built for one RID). The props deliver
them to a consuming asm program (`AsmReferencedProgram` items), and the targets give a .NET
consumer `BinariesCopyPath`. A program's `ProjectReference` and `PackageReference` items
default to `PrivateAssets="all"`: it carries what it needs, so its package depends on nothing.
A shared library's references to `StaticLibrary` projects default to `PrivateAssets="all"`
too, set when MSBuild restores it, because their code is linked in; `PrivateAssets="none"` keeps
one a dependency. Not with `AsmProduceStaticLibrary`, whose archive's users need the static
libraries it links.

## Toolchain properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmToolchain` | `Bundled` | `Bundled`: NASM, `llvm-mc`, `lld` (run as `-flavor gnu`, `link` or `darwin`) and `llvm-ar` from the toolchain package. `System`: the commands below, from PATH. Any other value fails with `ASM0002`. |
| `AsmToolchainVersion` | `$(AsmFoundationDefaultToolchainVersion)`, which is `0.2.0` for SDK 0.3.0 | the toolchain package version to download. When it is empty, no download is added. 0.1.0 has no `llvm-mc` (`ASM0004`). |
| `AsmHostRid` | from `NETCoreSdkRuntimeIdentifier`: `win-<arch>`, `osx-<arch>`, `linux-<arch>` or `linux-musl-<arch>` | the build machine. Distribution RIDs (`ubuntu.24.04-x64`, `alpine.3.20-x64`) become portable ones. |
| `AsmToolchainPackageId` | `AsmFoundation.Toolchain.$(AsmHostRid)` | packages exist for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` and `osx-arm64`. Elsewhere the build stops with `ASM0006`. |
| `AsmToolchainDirectory` | `<NuGetPackageRoot>/asmfoundation.toolchain.<rid>/<version>/tools/` | set it to use tools from another folder: no download happens, and the folder must hold `nasm`, `llvm-mc`, `lld` and `llvm-ar` (`.exe` on Windows). |
| `AsmNasmCommand`, `AsmLlvmMcCommand`, `AsmLinkerCommand`, `AsmArchiverCommand` | `nasm`, `llvm-mc`, `ld`, `ar` | the commands used with `AsmToolchain=System`. They may include arguments. `ld` links ELF of the build machine's own architecture only: Windows and macOS RIDs need `AsmLinkerCommand` set to `lld-link` or `ld64.lld`, and the Linux RID of the other architecture `ld.lld` or a cross `ld` (`ASM0005`). |

## Properties the SDK sets for you

Read-only in practice.

**In `Sdk.props`, before the project body:**

| Property | Value | Why |
|---|---|---|
| `UsingAsmFoundationSdk` | `true` | detect asm projects in `Directory.Build.*`, targets and package props |
| `DefaultProjectTypeGuid` | `{13B669BE-BB05-4DDF-9536-439F39A36129}` | the generic project type of SDK-style projects (Visual Studio's Common Project System). `dotnet sln add` reads it and writes it as the entry's `Type`; without it the CLI printed an unknown project type and added nothing, with exit code 0 |
| `AsmFoundationSdkDirectory` | the SDK package root | where `Sdk/`, `targets/`, `include/` and `stubs/` live |
| `EnableDefaultItems` | `false` | no managed `Compile`, `None` or `EmbeddedResource` globs. The managed `None` glob removes `**/*$(DefaultLanguageSourceExtension)` right after including `**/*`, and an `.asmproj` has no language extension, so it would remove every file again. The SDK declares its own `None` glob instead (`EnableDefaultNoneItems`). |
| `DisableImplicitFrameworkReferences`, `AutomaticallyUseReferenceAssemblyPackages` | `true`, `false` | no NETStandard.Library reference |
| `GenerateAssemblyInfo`, `ProduceReferenceAssembly`, `GenerateDependencyFile`, `GenerateRuntimeConfigurationFiles`, `UseAppHost` | `false` | nothing managed exists |
| `IncludeBuildOutput`, `CopyBuildOutputToOutputDirectory`, `CopyOutputSymbolsToOutputDirectory` | `false` | the native outputs are written and packed by the SDK's own steps |
| `SkipCopyBuildProduct` | `true` | the same reason |
| `NoWarn` | `+NU5128` | a native package's payload is outside `lib/` by design |
| `PublishAot`, `PublishTrimmed`, `PublishSingleFile`, `PublishReadyToRun`, `SelfContained` | `false`, and `TreatAsLocalProperty` on `Sdk.props` | a .NET app's publish switches on the command line (`dotnet publish -p:PublishAot=true`) are global properties, and NuGet's restore evaluates every project with them: Microsoft.NET.Sdk then stopped each asm project's restore (`NETSDK1207` for AOT, `NETSDK1124` for trimming). Local from `Sdk.props` on, they are `false` for everything after it; projects built from here still inherit the original values |

**In `Properties.targets`, after the project body:**

| Property or item | Value | Why |
|---|---|---|
| `TargetFramework` | `freestanding`, when neither `TargetFramework` nor `TargetFrameworks` is set | the default target framework |
| `TargetFrameworkIdentifier`, `TargetFrameworkVersion` | `.NETStandard`, `v1.0` (freestanding) or `v2.0` (libc) | the NuGet framework behind the alias; Microsoft.NET.Sdk then never parses the alias |
| `RuntimeIdentifiers` | all ten RIDs | restore writes a target per RID listed, and an inner build for a RID restore skipped fails with `NETSDK1047`. The project's own list was read first |
| `IsRidAgnostic` | `false` | a referencing project passes its RID on |
| `LanguageTargets` | `Microsoft.Common.CrossTargeting.targets`, in the outer build of a `TargetFrameworks` project | Microsoft.NET.Sdk gives an unknown project extension the single-target common targets even there, and the outer build fails |
| `CheckNotRecommendedTargetFramework` | `false` | no "netstandard1.x is not recommended" warning: nothing managed is built for it |
| `OutputType` | `Library` | hidden: the managed SDK would build an apphost for `Exe` |
| `DebugType`, `DebugSymbols` | `none`, `false` | hidden: the managed SDK would expect a PDB that nothing writes |
| `DisableImplicitFrameworkDefines` | `true` | hidden: the managed SDK would append `NETSTANDARD2_0` and the like to `DefineConstants` |
| `PackageReference` `AsmFoundation.Sdk` | `$(AsmFoundationSdkVersion)`, implicit | brings `buildTransitive/AsmFoundation.Sdk.targets` to referencing .NET projects (the `runtimes/<rid>/native/` copies and the `deps.json` entries). Off with `DisableAsmConsumerTargets` |

## Items and their metadata

| Item | Default | Meaning and footguns |
|---|---|---|
| `Compile` | `**/*.asm` and `**/*.s` (`EnableDefaultCompileItems`) | the sources, with .NET's semantics: `<Compile Remove="old/**" />`, `<Compile Include="../shared/x.asm" />`. Only `.asm` and `.s` (`ASM0025` for another). For each RID, the SDK picks one file per name by extension and qualifier (see [Runtime identifiers](#runtime-identifiers)), and runs NASM or llvm-mc once on it. An `.asm` that another file `%include`s is a source too, which gives `duplicate symbol`: rename it `.inc`, or `<Compile Remove="x.asm" />`. Listing a source the default already includes is `ASM0024`. 0.3.0's `AsmSource` is `ASM0110`. |
| `None` | every other file of the project folder, `**/*.inc` among them (`EnableDefaultNoneItems`) | what IDEs show besides the sources, which are removed from it, so a file is never both. Each `None` item ending in `.inc` is a build dependency of every source (editing one reassembles every source), packed under `src/` with `AsmPackSources`. **List include files from folders outside the project here too**, `<None Include="../common/include/**/*.inc" />`, or edits to them will not rebuild. 0.3.0's `AsmInclude` is `ASM0111`. |
| `CustomAdditionalCompileInputs` | none | .NET's item for more inputs of the compile: any other file a source reads (`%incbin` data, a `.mac` file), so editing it reassembles. |
| `AsmIncludeDirectory` | `include/` (when it exists), then the project folder; the SDK's `include/` last | the `-I` paths. Items without `AsmOrigin` come first, in the order added; items from packages and references after; the SDK's own folder (`asmsdk.inc`, `asmsdk-gnu.inc`) last, so a project file of the same name wins. NASM tries the working directory, which is the project folder, before any of them. |
| `LinkerArg` | none | extra linker arguments, NativeAOT's item name, appended to every link line in order: `<LinkerArg Include="-z defs" />`. One item may hold several words; they are not quoted, so a path with spaces must carry its own quotes. **Footguns:** NativeAOT hands `LinkerArg` to clang (`-Wl,…`), and the SDK runs the linker directly, so write the linker's own syntax; and each OS's linker has its own (ld.lld: `-z defs`; lld-link: `-opt:ref`; ld64.lld: `-dead_strip`), so condition an item on `$(RuntimeIdentifier)`. |
| `NativeLibrary` | none | extra link inputs (`.a`, `.lib`, `.so`, `.dylib`, `.o`), in order, after your objects: `<NativeLibrary Include="../third_party/libz.a" />`, NativeAOT's item for the same inputs. References and packages add theirs here. A program copies the shared libraries that references and packages add next to itself; one the project names itself (`/usr/lib/libz.so`) stays where it is. 0.3.0's `AsmLinkLibrary` is `ASM0109`, and so is a package packed by 0.3.0, whose props still add it. |
| `AsmLibcSymbol` | none | extra C function names for the C library stand-in (`TargetFramework=libc`): the Linux stub, `libSystem.tbd` (the underscore is added) or the UCRT import library (the name must be a `ucrtbase.dll` export). A program fails with `undefined symbol` until the function is listed; so does a Windows DLL or a dylib. A Linux shared library links anyway, and the function binds at load time. |
| `AsmPublicIncludeDirectory` | `include/` when it exists, else the project folder | what a referencing `.asmproj` adds to its `-I` path. Declaring any item replaces the default. |
| `AsmPublicInclude` | `include/**/*`, only when `include/` exists | the files packed into the package's `include/`. **Without an `include/` folder, the default is empty** and the package has no include files. Declaring any item replaces the default; packed paths are `include/%(RecursiveDir)%(Filename)%(Extension)`. |
| `PackageDownload` | `AsmFoundation.Toolchain.<host-rid>` at `[<version>]` | added when `AsmToolchain=Bundled`, `AsmToolchainDirectory` is empty, `AsmToolchainVersion` is set and the build machine has a package. A `PackageDownload` is not a dependency of your package. |
| `AvailableItemName` | none | the item types an IDE shows besides its built-in ones, and offers as build actions. The sources and include files are `Compile` and `None` items, the IDEs' own types, so the SDK declares none (0.2.0 and 0.3.0 declared their `AsmSource` and `AsmInclude`, without which Rider showed an `.asmproj` empty). |
| `UpToDateCheckInput`, `UpToDateCheckBuilt` | the `Compile` items, the `None` items ending in `.inc`, `CustomAdditionalCompileInputs` and the `ExportsFile`; the output of each RID (unless `Objects`) | what the fast up-to-date checks of Visual Studio and Rider compare, so that editing a source makes the IDE build. `Visible="false"`: they name files that are items already. |

**Metadata the SDK reads or writes:**

| Metadata | On | Values |
|---|---|---|
| `AsmOrigin` | `AsmIncludeDirectory`, `NativeLibrary` | `Package`, `Project` or `Sdk`. A directory with any `AsmOrigin` is ordered after local ones, and its `**/*.inc` become build dependencies. |
| `AsmPackage` | the same | the package id that contributed the item |
| `AsmProject` | the same | the referenced project's name |
| `AsmItemType` | items returned by `GetAsmLinkInputs` | `IncludeDirectory` or `LinkInput` |
| `ObjectFile`, `ObjectToolPath`, `SourceToolPath` | the sources selected for the RID, computed by `AsmPrepare` | `$(AsmIntermediateDirectory)obj/%(RecursiveDir)%(Filename).o` (the qualifier stays in the name: `io.linux.o`), and the absolute forward-slash paths. The assembler is handed `AssembledToolPath`: the source's path relative to the project folder (`SourceRelativeToolPath`), or that of the wrapper the SDK writes in `obj/` (`SourceWrapper`: a GNU-syntax source with `ImplicitUsings`, a WebAssembly source), so no object records the checkout folder |
| `RuntimeIdentifiers` | a `ProjectReference` to an `.asmproj`, in a .NET project | the RIDs a portable build of the .NET project carries (passed on as `AsmRequestedRuntimeIdentifiers`) |
| `BinariesCopyPath` | a `ProjectReference` to an `.asmproj`, or a `PackageReference` to an asm package, in a .NET project | where that library goes in the .NET project's output and publish folder: `Auto`, `Runtimes`, `AppFolder`, `RidFolders`, or a folder with an optional `{rid}` (see [Where the binaries go](guide/interop.md#where-the-binaries-go-binariescopypath)). Wins over the project's `BinariesCopyPath` property |
| `PrivateAssets` | a `ProjectReference` to an `.asmproj`, in a .NET library | `all`: `dotnet pack` puts every RID's library into the .NET package (`runtimes/<rid>/native/`) instead of depending on the asm package, and the reference does not flow to referencing projects |
| `AsmRuntimeFile`, `AsmRuntimeSymbols` | a shared link input from `GetAsmLinkInputs` or a package's props | the file a program loads at run time (on Windows the DLL behind the import library it links) and its PDB: what the program copies next to it |
| `AsmBundlePath`, `AsmProgramName` | a program's files, from `GetAsmLinkInputs` (`AsmItemType` `Program`), `GetAsmNativeRuntimeAssets` (`AsmKind` `Program`) or a package's `AsmReferencedProgram` items | each file's path relative to the program, which a consumer keeps under the folder it puts the program in, and the program's file name |

**In a referencing .NET project:**

| Property | Default | Meaning and footguns |
|---|---|---|
| `BinariesCopyPath` | `Auto`: `runtimes/<rid>/native/` in a portable build, the app folder in a RID-specific one | where every asm library of the project goes (its asm references, the asm libraries its .NET references bring, and asm packages), unless a reference says otherwise. A folder without `{rid}` in a build of several RIDs of one OS stops with `ASM0044`. Not passed to the asm projects: they build the same files whatever it says |
| `AsmGenerateNativeLibraryLoader` | `true` | `false`: no generated fallback loader (`obj/.../AsmNativeLibraryLoader.g.cs`). Then a NativeAOT program finds a library outside its own folder only through a resolver of yours |
| `AsmGenerateBindings` | `true` | `false`: no C# bindings at all, neither the written baselines (a project reference's or a package's) nor the generator. For a project with imports of its own |
| `AsmBindingsNesting`, `AsmBindingsSpanOverloads`, `AsmBindingsVisibility`, `AsmBindingsClassKind`, `AsmBindingsVirtual`, `AsmBindingsSealed`, `AsmDefaultBindings` | `Files`, `true`, `Internal`, `Static`, `false`, `false`, `true` | how every library's [baseline](guide/interop.md#generated-bindings) is laid out and declared, below an `AsmBindings` item and below the code's `[assembly: AsmLibrary(Default...)]`. A value that is no option's, or options C# cannot follow together (`ProtectedInternal`, `Virtual` or `Sealed` with `Static`; `Virtual` with `Sealed`), is `ASM0146`. Not passed to the asm projects. An environment variable of the same name is read as the property |

**Items in a referencing .NET project:**

| Item | Metadata | Meaning and footguns |
|---|---|---|
| `AsmBindings` | `Namespace`, `Nesting`, `SpanOverloads`, `Visibility`, `ClassKind`, `Virtual`, `Sealed`, `DefaultBindings` | one library's baseline, by its `AssemblyName` (`<AsmBindings Include="hello" Namespace="Acme.Native" />`): wins over the `AsmBindings*` properties, loses to the code's attributes. An item for a library the project has no bindings for is `ASM0140`; the same values and rules as the properties (`ASM0146`) |

## Targets

| Target | Runs | Notes |
|---|---|---|
| `_AsmCheckProject` | before restore takes package references, and before the build's configuration check | raises `ASM0003` for an unknown target framework, `ASM0100` to `ASM0108` for settings removed in 0.2.0, and `ASM0109` to `ASM0112` for the items and switch renamed after 0.3.0 |
| `_AsmCheckRuntimeIdentifiers` | before the RID outer build dispatches, and before an inner build prepares | raises `ASM0010`, `ASM0011` and `ASM0012`; a project with no RID builds the build machine's RID, and `ASM0013` warns a publish or pack (on a machine that is none of the eight, a solution build builds nothing for it, with a message) |
| `_AsmBuildEachRuntimeIdentifier` | the RID outer build's `Build` | builds the project once per RID, with `RuntimeIdentifier` as a global property |
| `_AsmCleanEachRuntimeIdentifier` | after `Clean`, in the outer build of each framework | cleans each RID's inner build |
| `_AsmResolveToolchain` | before `AsmPrepare` | locates the tools, raises `ASM0001`, `ASM0002` and `ASM0005`, and runs `chmod +x` on the bundled tools on Linux and macOS (NuGet drops the execute bit) |
| `AsmPrepare` | before assembling, linking and packing | selects the sources of the RID (`AsmSelectSources`: `ASM0020` to `ASM0024`), computes object paths, the ordered include directories, dependencies, defines, link inputs, and the `ExportsFile` and `LinkerArg` arguments; depends on `_AsmResolveProjectReferences` |
| `AsmAssemble` | once per out-of-date source (target batching) | NASM or llvm-mc (`ASM0004` without llvm-mc). Inputs: the source, every dependency `.inc`, the project file, the SDK's targets and `assemble-options.txt`. Output: the object. A failed run deletes the object and raises `ASM0007` (after `ASM0019` when llvm-mc's WebAssembly object writer stopped) |
| `_AsmReadStructs` | before `AsmAssemble` and the wrappers, every build, not for WebAssembly | reads the `STRUCT` lines of each source and of the files it includes (`AsmReadStructs`), lays each struct out as C does on the RID, and writes the source's constants to `obj/.../asm/structs/<path>.inc`: `%assign` lines for NASM, passed as a second pre-include (`-P`), `.set` lines for llvm-mc, which the source's wrapper includes (a GNU source with a `STRUCT` gets a wrapper even with `ImplicitUsings` off). A file is rewritten only when its text changes, and the files are inputs of `AsmAssemble`. `ASM0130` and `ASM0136` at the line |
| `_AsmWriteSourceWrappers` | before `AsmAssemble`, with `ImplicitUsings`, always for WebAssembly, and for a GNU source with `STRUCT` lines | writes `obj/.../asm/implicit/<path>.s` for each GNU-syntax source of the RID that needs one (`AsmWriteSourceWrappers`): a WebAssembly build's defines as labels (`ASM0018`), the `ImplicitUsings` include, the source's `STRUCT` constants, then the source's own text behind a line marker with its full path, so diagnostics and DWARF name the source and its lines; only when its text changes (`ASM0026` when a source cannot be read) |
| `_AsmWriteAssembleOptions` | before `AsmAssemble` | writes both assemblers' options (formats, warnings, defines, include folders, `ImplicitUsings`, `AsmFlags`, `AsmLlvmMcFlags`) to `obj/.../asm/assemble-options.txt`, only when they change: an input of `AsmAssemble`, so an option set in `Directory.Build.props` or on the command line assembles again, as `CoreCompileInputs.cache` does for C# |
| `_AsmBuildSystemLibraries` | linked outputs that use the C library, and every Windows one | writes the stand-ins from the SDK's symbol lists: the Linux stub and `libasmsdk_nonshared.a`, `libSystem.tbd`, the `kernel32.lib` and `ucrtbase.lib` import libraries and `asmsdk_winstdio.lib` |
| `_AsmBuildCrt` | `libc` executables with no `StartupObject`, on Linux and Windows | `stubs/crt1.asm` or `crt1-arm64.s` → `crt1.o`; `stubs/wincrt-<arch>` → `wincrt.obj` |
| `_AsmReadApi` | between `AsmAssemble` and `AsmLink` | reads each object's API records (a `PROTO` line, a `STRUCT` line, a function macro), lays the structs out, checks them (`AsmReadApi`: `ASM0130` to `ASM0132`, `ASM0134` to `ASM0136`) and writes `<AssemblyName>.api.json` into `obj/` and beside the RID's binary; no `PROTO` and no `STRUCT` line, no file. WebAssembly objects carry the records in their `asmapi` custom section, each function's record with its whole wasm type, which `ASM0132` compares exactly |
| `AsmLink` | unless `Objects` | links (`ASM0015` for a macOS freestanding program), then checks a Windows or macOS image (`AsmCheckImage`: `ASM0080` to `ASM0082`). Inputs: objects, link inputs, the `ExportsFile`, the project file. Outputs: `$(TargetPath)` and the static library |
| `CoreCompile` | replaced, skipped in design-time builds | `AsmAssemble;AsmLink` in an inner build, the RID dispatch in the outer one; nothing when `DesignTimeBuild` or `SkipCompilerExecution` is `true`: IDEs call `Compile` and `CoreCompile` with both on every project load, and a design-time build must not run the tools (see [IDEs](guide/ides.md)) |
| `CreateManifestResourceNames` | replaced, empty | no resources |
| `CopyFilesToOutputDirectory` | replaced | nothing managed to copy; prints `<Project> -> <output>` once per RID |
| `GetTargetPathWithTargetPlatformMoniker` | replaced, returns nothing | so a referencing `.csproj` adds no assembly reference (a native file would raise `MSB3246`) |
| `_AsmMergeApi` | the RID outer build, after `_AsmBuildEachRuntimeIdentifier` | asks each RID's build for its API file (`GetAsmApiFile`), checks that every RID declares the same functions the same way (`AsmMergeApi`: `ASM0133`), writes the library's `<AssemblyName>.api.json` in `bin/<Configuration>/<tfm>/`; the WebAssembly RIDs take part like the others (one API for every RID) |
| `_AsmWriteRidCHeader`, `_AsmWriteMergedCHeader` | after `_AsmReadApi` (each RID) and after `_AsmMergeApi` (the RID outer build) | write `<AssemblyName>.h` beside the API file they follow (`AsmWriteCHeader`), or delete it when there is no API file or the header is off (`AsmGenerateCHeader=false`, or a project's own `include/<AssemblyName>.h`: `_AsmComputeCHeader`) |
| `GetAsmApiFile` | called after a build | the API file of this build, with `RuntimeIdentifier` metadata (an inner build's `obj/` copy, which names each function's source; the merged file in the RID outer build) |
| `GetAsmLinkInputs` | called by referencing `.asmproj` projects | returns the public include directories, then the static library, else the shared one (a DLL's import library), else (for `Objects`) the objects' absolute paths |
| `_AsmResolveProjectReferences` | in the referencing project | calls `GetAsmLinkInputs` on each referenced `.asmproj`, for this build's RID and the target framework NuGet chose; references to other project types are left alone |
| `_AsmSkipAsmReferencesInRidOuterBuild` | in the RID outer build | keeps the outer build from building its asm references with no RID: each inner build resolves them for its own |
| `GetAsmRuntimeIdentifiers`, `GetAsmConsumerInfo` | called by the consumer targets | the project's own RID list; its output kind, RIDs and import name (`AssemblyName`), asked without a RID |
| `GetAsmNativeRuntimeAssets` | called by the consumer targets and the copy targets | each RID's shared library (and PDB) with `runtimes/<rid>/native/` as its default relative path, its RID, project, library name and version, and import name |
| `GetCopyToOutputDirectoryItems`, `GetCopyToPublishDirectoryItems` | called by a referencing project's common copy targets, when that project does not deliver the files itself (the consumer targets below mark asm references `Private="false"` and deliver them laid out) | the shared library: flat in a RID-specific build, under `runtimes/<rid>/native/` in a portable one |
| `_AsmComputePackLayout`, `_AsmGetRidPackContent`, `_AsmPackNativeContent` | `dotnet pack`, once per target framework (`TargetsForTfmSpecificContentInPackage`) | the package paths per RID, the consumer props, the .NET consumer targets of a package with a shared library (`AsmWritePackageConsumerTargets`), the `_._` marker; maps include files, sources, libraries, props and targets into the package |
| `_AsmRemoveSdkPackageDependency` | after `GenerateNuspec` | removes the implicit `AsmFoundation.Sdk` dependency from the `.nuspec` (`ASM0050` if it cannot) |

**In a referencing .NET project** (`buildTransitive/AsmFoundation.Sdk.targets`):

| Target | Runs | Notes |
|---|---|---|
| `_AsmConfigureAsmProjectReferences` | after `_GetProjectReferenceTargetFrameworkProperties` | per asm reference: drops a non-shared-library reference with a message (a `StaticLibrary` reference in a `PublishAot` build for one RID is kept, built for that RID and linked, see `_AsmLinkStaticLibrariesIntoNativeAot`; an optional asm project without a serving RID is dropped with `ASM0049`); a RID-specific build gets the RID that serves it best (`AsmSelectRuntimeIdentifier`, `ASM0040`, `ASM0041`); a portable build passes the reference's `RuntimeIdentifiers` metadata on; the app's publish properties (`SelfContained`, `PublishAot`, …), output folder (`OutputPath`, `OutDir`) and its own `BinariesCopyPath` and `AsmGenerateNativeLibraryLoader` are not passed; marks the reference `Private="false"`, so the common copy targets leave its files to `_AsmAddNativeCopyItems` (a `Private="false"` the project wrote itself is kept as `AsmCopyLocal`, and still means no copies); records each library and its `BinariesCopyPath` metadata |
| `_AsmResolveNativeLayouts` | before `CoreCompile` and the copies | the folder of each asm library (`AsmResolveNativeLayouts`): the reference's `BinariesCopyPath`, then the property, then what the .NET references report (`GetAsmNativeLibraries`), then `Auto` (`ASM0043`, `ASM0046`) |
| `_AsmAddBindings` | after `ResolveProjectReferences`, in a C# project for .NET 7 or later | asks each asm library reference (not a program: it is started, never loaded) for its API file (`GetAsmApiFile`). Without the switch, `AsmWriteBindings` writes each library's baseline (`obj/.../AsmBindings/<Namespace>.g.cs`) as a `Compile` item; with it, each API file becomes an `AdditionalFiles` item (`AsmApiFile="true"`, the library's `AsmBindings` item as `AsmBindings*` metadata) and this SDK's generator (`bindings/netstandard2.0/AsmFoundation.Sdk.Bindings.dll`) a candidate. Sets `AllowUnsafeBlocks=true`, adds each library as an `_AsmBindingsLibrary` item; off with `AsmGenerateBindings=false` |
| `_AsmBindingsSwitch`, `_AsmCheckBindingsItems`, `_AsmPickBindingsGenerator` | the first when a bindings target asks; the others before `CoreCompile` | the bindings' shared targets, in the SDK's consumer targets and, word for word, in every asm package with bindings: MSBuild keeps the last definition imported, so their names and meanings never change. The switch (`_AsmBindingsGenerate=true` when a `Compile` item says `AsmLibrary`), `ASM0140` for an `AsmBindings` item that names no `_AsmBindingsLibrary`, and the newest `_AsmBindingsGeneratorCandidate` (by its `Version`) as the one `Analyzer` |
| `_AsmPackageBindingsTokens_<id>`, `_AsmPackageBindings_<id>` (in an asm package's targets) | before and after the .NET SDK's `RunProduceContentAssets` | the package's own: the tokens of its baseline from the consumer's settings (checked, `ASM0146`), and, with the switch, its baseline out of the compilation, its API file and generator in |
| `_AsmLinkStaticLibrariesIntoNativeAot` | after `ResolveProjectReferences` | a `PublishAot` build for one RID: asks each kept `StaticLibrary` reference for its `GetAsmLinkInputs` (from the build that just ran) and adds its archive as a `NativeLibrary` item; its `DirectPInvoke` item (the library's `AssemblyName`) was added with the reference |
| `_AsmCheckWasmNativeBuild` | after `_AsmConfigureAsmProjectReferences`, in a .NET WebAssembly app with asm references to link | `ASM0054` when .NET's native WebAssembly build is off for the app's .NET version (`UsingBrowserRuntimeWorkload`, `UsingWasiRuntimeWorkload`), before anything is built; the message names the workload, or the SDK, that turns it on. A package of a wasm library makes the same check from its props |
| `_AsmWarnWasmBuildInterprets` | after `_AsmConfigureAsmProjectReferences`, in a build (not a publish) of a .NET browser app with asm references to link | `ASM0055` (a warning, once per build) when `RunAOTCompilation` is `true` and `RunAOTCompilationAfterBuild` is not: .NET compiles ahead of time only in a publish, so the build interprets the C#. A package of a wasm library makes the same check from its props, and whichever runs first says it |
| `_AsmAddWasmNativeFileReferences` | before `_InitializeCommonProperties`, the first step of .NET's WebAssembly build | a .NET WebAssembly app: asks each linked asm reference for its `GetAsmLinkInputs` (the archive, or the objects) and adds them as `NativeFileReference` items (`ScanForPInvokes="false"`), with the reference's `AssemblyName` as a P/Invoke module (`_WasmPInvokeModules`) |
| `GetAsmNativeLibraries`, `GetAsmNativeAssets` | called by a referencing .NET project | the asm libraries this project deploys with its explicit choices; the asm files it knows (its asm references' and, recursively, its .NET references'), matched by full path because the .NET SDK drops custom metadata from the copy items |
| `_AsmCollectKnownNativeAssets`, `_AsmComputeDeployedNativeAssets` | after the references are built | the asm files this project may receive, and where each goes (`AsmLayOutNativeAssets`: in a RID-specific build only the best-serving RID's, `ASM0044` for two files on one path). Nothing for a .NET WebAssembly app, which links its asm code and deploys no native file |
| `_AsmAddNativeAssetsToBuildDepsFile`, `_AsmAddNativeAssetsToPublishDepsFile` | after the `deps.json` is generated, for build and publish | add each library as a project library, so the .NET host finds the right one: `runtimeTargets` in a portable app, `native` assets in a RID-specific one, each with `localPath` (`ASM0042` if the file cannot be read) |
| `_AsmComputeDeliveredNativeAssets` | with the copy and publish lists | the files of this project's own asm references, each at its `BinariesCopyPath` place (`AsmLayOutNativeAssets`); a RID-specific build keeps only the best-serving RID's |
| `_AsmAddNativeCopyItems`, `_AsmAddNativePublishItems` | before `GetCopyToOutputDirectoryItems`, before `GetCopyToPublishDirectoryItems` | put those files into the copy and publish lists before the two targets compose what they hand a referencing project, so it receives them laid out, even a project without these targets (an app that gets a .NET library whose asm reference is `PrivateAssets="all"`); the publish items carry `ExcludeFromSingleFile="true"`, which keeps them beside a single-file bundle where nothing marks them `AssetType=native` |
| `_AsmLayOutNativeCopies` | after `GetCopyToOutputDirectoryItems` | every asm file of the copy lists, whichever way it came (its own references, or a RID-agnostic class library that hands every RID's copy on), once, at its `BinariesCopyPath` place; a RID-specific build keeps only its RID's. Shapes this project's own output: what it hands on was fixed before |
| `_AsmLayOutNativePublishFiles` | after `ComputeResolvedFilesToPublishList` | the same for publish, and `AssetType=native` on each asm library (clearing the delivery's `ExcludeFromSingleFile`), which the .NET SDK's single-file step decides by (beside the bundle, or inside it with `IncludeNativeLibrariesForSelfExtract`) |
| `_AsmGenerateNativeLibraryLoader` | before `CoreCompile`, C# for .NET 5 or later | writes `obj/.../AsmNativeLibraryLoader.g.cs` (`AsmWriteNativeLibraryLoader`, `ASM0045`) when a library goes anywhere but the app folder, unless `AsmGenerateNativeLibraryLoader=false` |
| `_AsmEmbedNativeAssetsInPackage` | `dotnet pack` of a .NET library (`TargetsForTfmSpecificContentInPackage`) | for each asm reference with `PrivateAssets="all"`: every RID's library under `runtimes/<rid>/native/` of the package, and `buildTransitive/<tfm>/<id>.targets` for its consumers |

**In a .NET project that references a package with asm libraries**
(`buildTransitive/<tfm>/<id>.targets` of the package, plain MSBuild, every name suffixed with
the package id): `_AsmPackageLayout_<id>` reads `BinariesCopyPath` from the `PackageReference`
or the property (`ASM0043`); `_AsmPackageBinariesCopyPath_<id>` moves the package's native
items after `ResolvePackageAssets` (`ASM0044`), and the .NET SDK writes the new `localPath`
into `deps.json` itself; `_AsmPackageNativeLoader_<id>` writes the package's fallback loader
for a C# project.

**In a .NET browser app's build:** the consumer targets set
`WasmDebugLevel` when the app sets none, `0` in Release and `-1` in Debug, so that a Release
build runs without .NET's debugger in Chromium and Firefox; other configurations and every
publish keep .NET's value. A package of a `browser-wasm` library sets the same from its props,
in `_AsmWasmDebugLevel_<id>` (before `PrepareForBuild`: props come before the app's project
body, where its `RuntimeIdentifier` is not known yet), next to `_AsmWarnWasmBuildInterprets_<id>`
(`ASM0055`) and `_AsmCheckWasmNativeBuild_<id>` (`ASM0054`). See
[WebAssembly](guide/webassembly.md#in-a-net-webassembly-app).

## Files the build writes

| Path | What |
|---|---|
| `bin/<Configuration>/<tfm>/<rid>/` | the outputs: the library or program, a Windows DLL's `<name>.dll.lib`, a Windows program's `<name>.lib` when its `ExportsFile` exports, the `.pdb` of a Windows debug build |
| `obj/<Configuration>/<tfm>/<rid>/asm/obj/<subfolder>/<file>.o` | one object per selected source |
| `obj/<Configuration>/<tfm>/<rid>/asm/api/<AssemblyName>.api.json`, `bin/<Configuration>/<tfm>/<rid>/<AssemblyName>.api.json` | the RID's API file, from its `PROTO` lines (only when it has some) |
| `bin/<Configuration>/<tfm>/<AssemblyName>.api.json` | the library's API file, written by the RID outer build when every RID agrees |
| `bin/<Configuration>/<tfm>/<AssemblyName>.h`, `bin/<Configuration>/<tfm>/<rid>/<AssemblyName>.h` | the C header beside each API file (`AsmGenerateCHeader`), the same text in each: every RID agrees on the API |
| `obj/<Configuration>/<tfm>/<rid>/asm/stubs/` | the stand-ins and startup code of this RID: `libc-stub.asm` or `.s`, `libc.so.6`, `libc.musl-x86_64.so.1` or `libc.musl-aarch64.so.1`, `libasmsdk_nonshared.a`, `crt1.o` (Linux); `libSystem.tbd` (macOS); `kernel32.def`, `kernel32.lib`, `ucrtbase.def`, `ucrtbase.lib`, `asmsdk_winstdio.lib`, `wincrt.obj` (Windows) |
| `obj/<Configuration>/<tfm>/asm/pack/<PackageId>.props`, `<PackageId>.targets`, `_._` | the consumer props, the .NET consumer targets (a package with a shared library or C# bindings) and the `lib/` marker for the package |
| `obj/<Configuration>/<tfm>/asm/pack/<AssemblyName>.cs.pp` | the C# bindings' baseline the package carries (a library with `PROTO` or `STRUCT` lines), from the target framework a .NET consumer gets |
| `obj/<Configuration>/<tfm>/[<rid>/]AsmNativeLibraryLoader.g.cs` (in a referencing C# project) | the generated fallback loader |
| `obj/<Configuration>/<tfm>/[<rid>/]AsmBindings/<Namespace>.g.cs` (in a referencing C# project) | each asm library reference's baseline, written by the SDK while the project's code does not name `AsmLibrary`; deleted when it no longer applies |
| `bin/<Configuration>/<PackageId>.<Version>.nupkg` | `dotnet pack` output, unless `-o` or `PackageOutputPath` says otherwise |

The SDK registers every file in this table except the `.nupkg` in `FileWrites`, so
`dotnet clean` removes them, for every RID. A file that your own target generates is removed
by `dotnet clean` only if you add it to `FileWrites` too.

## Errors and warnings

| Code or text | Meaning | Fix |
|---|---|---|
| `ASM0001` | The bundled toolchain is not where restore puts it. | Run `dotnet restore`; don't build with `--no-restore` on a clean machine; or use `AsmToolchain=System` or `AsmToolchainDirectory`. |
| `ASM0002` | `AsmToolchain` is neither `Bundled` nor `System`. | Use one of the two. |
| `ASM0003` | `TargetFramework` is neither `freestanding` nor `libc`. | Use one of the two, or `TargetFrameworks=freestanding;libc`. |
| `ASM0004` | A `.s` source, or an ARM64 `libc` build, with a toolchain that has no `llvm-mc` (0.1.0). | `AsmToolchainVersion` 0.2.0 or later. |
| `ASM0005` | `AsmToolchain=System` without `AsmLinkerCommand`, for a Windows or macOS RID, or for a linked Linux RID of another architecture than the build machine's: the default `ld` links the machine's own ELF only. | `AsmLinkerCommand` `lld-link`, `ld64.lld` or `ld.lld` (or a cross `ld`). |
| `ASM0006` | No toolchain package for this build machine (Intel Mac, musl). | `AsmToolchain=System`, `AsmToolchainDirectory`, or `AsmHostRid`. |
| `ASM0007` | The assembler failed on a source; its object was deleted. | Read the assembler's line above it. |
| `ASM0010` | No RID to build: none listed, none asked for, no `-r`, on a build machine that is none of the eight RIDs (elsewhere its RID is the default). | `RuntimeIdentifiers`, a `ProjectReference`'s `RuntimeIdentifiers`, or `-r`. |
| `ASM0011` | A RID outside the ten (the eight native ones, `browser-wasm` and `wasi-wasm`). | One of those. |
| `ASM0012` | A RID asked for that the project does not list. | Add it, if the code supports it. |
| `ASM0013` (warning) | A `dotnet pack` of a project that lists no RIDs, or a portable publish or pack of a .NET project that references one, carries the build machine's RID alone. | List the platforms to ship in `RuntimeIdentifiers` (or on the `ProjectReference`), or publish with `-r <rid>`; `<NoWarn>ASM0013</NoWarn>` when one RID is the intent. |
| `ASM0015` | A freestanding program for macOS. | `TargetFramework=libc`, or a library. |
| `ASM0016` | A `libc` module or WASI program for `browser-wasm` or `wasi-wasm`: the bundled wasm-ld links no C library. | `freestanding`; or a `StaticLibrary`, which a .NET WebAssembly app links with its own C library. |
| `ASM0017` | A program (`Exe`) for `browser-wasm`, which starts no programs. | A `Library` for the browser, or the program for `wasi-wasm`. |
| `ASM0018` | A WebAssembly build got a define with a value (`NAME=3`), or one that is no symbol name: wasm sources receive defines as labels, which have no value. | Define the name alone. |
| `ASM0019` | llvm-mc's WebAssembly object writer stopped on a source ("LLVM ERROR: ..."): something a wasm object cannot hold. | Usually a `.set`/`.equ` constant, a data label without `ENDDATA`, or two functions in one section; see [WebAssembly](guide/webassembly.md#footguns). |
| `ASM0020` | Two files of one name apply equally to a RID. | Keep one, or qualify one more narrowly. |
| `ASM0021` | A `.asm` file qualified for ARM64 only, or for WebAssembly only. | ARM64 code goes in `.s` files, WebAssembly in `.s` files qualified `wasm`. |
| `ASM0022` | The source selection was asked for an unknown RID. | An internal check: report it. |
| `ASM0023` | No source builds a listed RID. | Add a source for it (ARM64: a `.s` file; WebAssembly: a `name.wasm.s` file), or remove the RID. |
| `ASM0024` | One source listed twice as a `Compile` item. | Remove the explicit item, or `EnableDefaultCompileItems=false`. |
| `ASM0025` | A `Compile` item that is not an asm source (`.c`, `.cs`): an `.asmproj` compiles `.asm` and `.s` only. | Make it a `None` item; build other languages in a project of their own. |
| `ASM0026` | A GNU-syntax source cannot be read, or a file the SDK generates cannot be written: a source's copy in `obj/`, a C# baseline, a C header. | Check the file and the folder's permissions. |
| `ASM0030` | The `ExportsFile` cannot be read, has an `extern "C++"` block, or a pattern on Windows or in a WebAssembly module's. | List exact names. |
| `ASM0040` | A RID-specific .NET build references an asm project none of whose RIDs serves its RID (a .NET WebAssembly app too, whose RID no other serves). | Add a serving RID (the message lists them); or `OptionalOnUnlistedRuntimes=true` in the asm project when the app copes without it. |
| `ASM0041`, `ASM0042` | The .NET SDK's RID graph, or the `deps.json`, cannot be read. | Repair the .NET SDK; rebuild. |
| `ASM0043` | A `BinariesCopyPath` value is not a preset or a folder inside the output: absolute, `..`, a character no OS takes in a file name, or a placeholder other than `{rid}`. | Use `Auto`, `Runtimes`, `AppFolder`, `RidFolders`, or a folder such as `native/{rid}`. |
| `ASM0044` | Two different files would be copied to one path: a folder without `{rid}` in a build of several RIDs of one OS (`win-x64` and `win-arm64` both give `x.dll`), or two libraries with one file name. | Add `{rid}` to the folder, narrow the reference's `RuntimeIdentifiers`, or give the libraries different names or folders. |
| `ASM0045` | The generated loader cannot be written. | Check the `obj/` folder's permissions. |
| `ASM0046` (warning) | Two .NET libraries the project references deploy one asm library with different `BinariesCopyPath` values; the first reported is used. | Set `BinariesCopyPath` on a reference to it in this project, or as this project's property. |
| `ASM0047` | A Windows program would keep a DLL it links in a subfolder: Windows has no run path. | `AppFolder` (or `Auto`) on Windows RIDs. |
| `ASM0048` (warning) | A program links an import library whose DLL its project or package does not name (a 0.3.0 package), so the DLL is not copied. | Repack the package with this SDK, or copy the DLL yourself. |
| `ASM0049` (warning) | A RID-specific .NET build references an asm project none of whose RIDs serves its RID, and the asm project says `OptionalOnUnlistedRuntimes=true`: the build goes on without it. | Nothing, when the app copes with the library's absence; otherwise add a serving RID to the asm project. |
| `ASM0050` | Pack cannot remove the SDK dependency from the `.nuspec`. | Report it. |
| `ASM0051` | A program to pack keeps libraries in a subfolder, which NuGet would flatten. | Keep them next to the program. |
| `ASM0052` | A .NET tool's settings file cannot be written. | Check the `obj/` folder's permissions. |
| `ASM0053` | `PackAsTool` on a project that is not a program, or that has several target frameworks. | `OutputType=Exe`, one `TargetFramework`. |
| `ASM0056` (not released yet) | `dotnet pack -r <rid>` of a .NET tool with `-p:PackageId=<id>` on the command line: that RID's package must be named `<id>.<rid>`, and a global property cannot be renamed. | `<PackageId>` in the project, or pack without `-r`: the top-level pack passes each RID package its id. |
| `ASM0054` | A .NET WebAssembly app links asm projects or an asm package, and .NET's native WebAssembly build is off for the app's .NET version, so the app would build without the asm code. | The workload the message names. On a later SDK: `wasm-tools-net10` for a `Microsoft.NET.Sdk.WebAssembly` app, the SDK of the app's own .NET (`global.json`) for a `Microsoft.NET.Sdk` browser app or a WASI app. |
| `ASM0055` (warning) | A build of a .NET browser app that links asm code sets `RunAOTCompilation`, which .NET acts on only when it publishes: the build interprets the C#. | Time a `dotnet publish`; `<NoWarn>$(NoWarn);ASM0055</NoWarn>` when the property is there for the publishes. |
| `ASM0060` | A package has no build for the consuming asm project's RID. | Build for a RID the message lists, or use a package packed for yours. |
| `ASM0061` | The consumer props or targets of a package cannot be written. | Check the `obj/` folder's permissions. |
| `ASM0070` | A C library stand-in cannot be written. | Check the `obj/` folder's permissions; report it otherwise. |
| `ASM0080` | A 64-bit Windows image needs a 32-bit absolute address fixup, which cannot work above 4 GB. The image was deleted. | `default rel`; `dq` in data tables. |
| `ASM0081` | A macOS image needs a fixup in a read-only segment (a text relocation). The image was deleted. | `lea rax, [rel label]`, `ADDRESS`; pointer tables in `.data`. |
| `ASM0082` | The linked image could not be read or parsed for the check. | Rebuild; report it if it persists. |
| `ASM0090` | `dotnet run` of a library, static library or object files project. | Run a program (`OutputType=Exe`). |
| `ASM0091` | No build of the program runs here: not natively, not under emulation, not through WSL or Docker. The message lists what it tried. | Build a RID this machine runs, install WSL, or start Docker. |
| `ASM0092` | `dotnet run --no-build` before a build. | Build first. |
| `ASM0093` | `AsmRunIn` is not `Auto`, `Host`, `Wasi`, `Wsl` or `Docker`. | One of those. |
| `ASM0100` to `ASM0107` | A setting of AsmFoundation.Sdk 0.1.0 that 0.2.0 replaced. Each error names its replacement: `AsmLibraryName` → `AssemblyName`, `AsmRuntime` → `TargetFramework`, `AsmDefineConstants` and `AsmDefine` items → `DefineConstants`, `AsmDebugInfo` → `DebugSymbols`, `AsmEntryPoint` → `StartupObject`, `AsmLinkFlags` → `LinkerArg` items and `ExportsFile`, `AsmOutputKind` → `OutputType`. | Make the change the error names. One build lists every removed setting. |
| `ASM0108` | The summary that stops the build after `ASM0100` to `ASM0107` (and `ASM0109`). | Fix those. |
| `ASM0110`, `ASM0111`, `ASM0112` | `AsmSource` items, `AsmInclude` items and `EnableDefaultAsmItems`, replaced after 0.3.0 by .NET's `Compile` items, `None` items (each `None` item ending in `.inc` is a dependency of every source; other extra inputs are `CustomAdditionalCompileInputs`) and `EnableDefaultCompileItems`. Each error shows the new line. | Make the change the error shows. |
| `ASM0130` | A `PROTO` or `STRUCT` line that does not parse, or names a type the SDK cannot bind (C's `int` and `long`, which differ between platforms; an unknown name; varargs; an array parameter; a struct passed by value). In a `STRUCT` line also: an array of pointers or structs (no C# fixed buffer holds one), a struct holding itself, a struct used before it is declared. | Write the fixed-width types, pointers and function pointers the message lists; take a pointer to a struct. |
| `ASM0131` | A `PROTO` line for no function of that RID, for a `LOCAL_FUNCTION`, or for a `HIDDEN_FUNCTION` of a shared library or program (no image exports it). | Declare the function with `FUNCTION` (a static library may describe a `HIDDEN_FUNCTION`). |
| `ASM0132` | The `PROTO` line's integer and pointer parameters differ from the `FUNCTION`'s count (which decides, on Windows, what is loaded from the stack); on WebAssembly, the `PROTO` line lowered to wasm types differs from the type the `FUNCTION` declares. | Make them agree. |
| `ASM0133` | Two RIDs declare a function or a struct differently (a struct: its declaration or its layout), or one does not declare it (per-RID source variants that drifted). | Declare it the same way in every RID's source. |
| `ASM0134` | Two `PROTO` lines for one function. | Keep one. |
| `ASM0135` | An object's API records, or an inner build's API file, cannot be read. | Rebuild; report it if it persists. |
| `ASM0136` | One struct declared twice, differently: in one source and an include file it reads (before assembling), or in two sources of the library (after). | Declare it once, in an include file every source that uses it includes. |
| `ASM0140` (the bindings) | `[AsmLibrary("x")]`, or an `<AsmBindings Include="x" />` item, names no asm library the C# project has bindings for (a reference or package with PROTO or STRUCT lines). | Use a library's `AssemblyName` (the message lists them). |
| `ASM0141` (the bindings generator) | A `Files` or `Folder` entry is not one of the library's files with PROTO lines. | Use a path the message lists (the extension and RID qualifier may be left out). |
| `ASM0142` (the bindings generator) | A second of the app's classes selects a function another binds (the baseline does not count: it keeps every function). | Select it once, or say `AliasBinding = true` for a copy. |
| `ASM0143` (the bindings) | An attributed class (or a class it is nested in) is not `partial`, or is generic; or a function is named like the app's class that binds it (C# forbids a member named like its class; a file's or folder's class takes the suffix `Asm` instead, as `AnswerAsm.Answer`); or two functions would get one C# name in one class; or, in a baseline, a class is named like a struct of the namespace's root, or like a namespace beside it (`simd.asm` and the folder `simd/`). | Make the class `partial` and not generic; bind one function in another class, or nest by file; lay the baseline out differently (`<AsmBindings Include="x" Nesting="..." />`, or `DefaultNesting`), or rename the file or the struct. |
| `ASM0144` (the bindings) | An API file cannot be read (a format the generator or the SDK's task does not know). | Rebuild the asm project, or repack the package, with the same SDK. |
| `ASM0145` (the bindings generator) | A public (or protected internal) copy binds a function that takes a struct, and the library's baseline, where the struct lives, is internal. | Make the baseline public (`DefaultVisibility = AsmVisibility.Public`, or `<AsmBindings Include="x" Visibility="Public" />`), or the copy internal. |
| `ASM0146` (the bindings) | A shaping value that is no option's (`AsmBindingsVisibility=Publik`), options C# cannot follow together (`ProtectedInternal`, `Virtual` or `Sealed` with `Static` classes; `Virtual` with `Sealed`), a `Default*` option on a class, `DefaultNamespace` on the global attribute, or a namespace C# cannot have. Checked by the generator, the SDK's task, or an asm package's targets, in the same words. | Use a value the message lists; set `ClassKind` to `Instance`; move `Default*` options to an assembly attribute, `DefaultNamespace` to the library's. |
| `ASM0147` (the bindings generator; warning) | The C# project compiles with a language version below 11, which the bindings need (`scoped`, function pointers): nothing is generated. | `<LangVersion>11</LangVersion>` or later, or `AsmGenerateBindings=false`. |
| `ASM0149` (`dotnet pack`; warning) | The project ships its own `include/<AssemblyName>.h`, so the package carries it, and the SDK wrote no C header from the `PROTO` and `STRUCT` lines. | Rename one of them, or set `AsmGenerateCHeader` to `false` to keep yours without the warning. |
| `ASM0148` (`dotnet pack` of an asm library; warning) | In one layout of the baseline the package carries, two names would meet (a class and a struct, a class and a namespace, two fields). The package is packed; a consumer that chooses that layout gets `ASM0143` as an `#error`. | Rename the file, folder or struct, so that every layout compiles. |
| `ASM0109` | An `AsmLinkLibrary` item, renamed `NativeLibrary` (NativeAOT's name) after 0.3.0. In the project, the error shows the new line; added by a package's props (a package packed by 0.3.0), it names the package. | `<NativeLibrary Include="..." />`; pack the named package again with this SDK. |
| `NU1201` | A project references an `.asmproj` whose target frameworks it cannot use: a `freestanding` project and a `libc`-only reference. | Make the reference target `freestanding` too (or both), or make this project `libc`. |
| `NU1202` | The same for a package. | The same, for the package's project. |
| `MSB3073 … exited with code 1` | The linker, or a stand-in's tool, failed. | Read the line above it. Add `<AsmFlags>-Xvc</AsmFlags>` so NASM's messages become MSBuild errors with file and line. |
| `MSB4236` / SDK not found | `global.json` or `nuget.config` does not lead to `AsmFoundation.Sdk`. | Check `msbuild-sdks` and the feeds (see [The toolchain](toolchain/index.md)). |
| `MSB4025 … ProjectType '' not found` | `.slnx` entry without a type. | Add `Type="13B669BE-BB05-4DDF-9536-439F39A36129"` (`dotnet sln add` writes it). |

[Troubleshooting](guide/troubleshooting.md) has the full messages, and the NASM and linker
errors.
