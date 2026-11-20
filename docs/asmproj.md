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
      `AsmFoundationSdkVersion=0.3.0` and `AsmFoundationDefaultToolchainVersion=0.2.0`.
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
| `linux-x64`, `linux-arm64`, `linux-musl-x64` | ELF, ld.lld | `lib<name>.so` | `lib<name>.a` | `<name>` |
| `osx-x64`, `osx-arm64` | Mach-O, ld64.lld | `lib<name>.dylib` | `lib<name>.a` | `<name>` (`libc` only) |

| Property | Default | Meaning and footguns |
|---|---|---|
| `RuntimeIdentifiers` | none | the RIDs the code supports, semicolon-separated. `dotnet build` builds each one in its own inner build. **There is no default:** a project that lists none is built for what its referencing projects ask for, or for `-r`, and otherwise fails with `ASM0010`. Restore always lists all seven, so the SDK overwrites the property after reading your list. |
| `RuntimeIdentifier` | none | one RID: what an inner build is for (`-r`, or a referencing project). In the project, a list of one. A RID the project's list does not have is `ASM0012`, and a RID outside the seven `ASM0011`. |
| `AsmRequestedRuntimeIdentifiers` | none | a global property the consumer targets set: the RIDs a `ProjectReference`'s `RuntimeIdentifiers` metadata asks for, comma-separated. They replace the project's list in its RID outer build. |
| `IsRidAgnostic` | `false` (set by the SDK) | tells referencing projects to pass their `RuntimeIdentifier` on, so a RID-specific build of a referencing project builds this project for its RID. |

- **Inner and outer builds.** With a `RuntimeIdentifier`, a build assembles and links that RID.
  Without one, it is the *RID outer build*: it builds each RID (in parallel when the build is)
  and links nothing itself, as the outer build of a multi-targeted project builds each
  framework. A solution build of a project that lists no RID builds nothing for it by itself,
  with a message, instead of `ASM0010`.
- **Sources per RID.** Every `AsmSource` competes per RID by file name: `.asm` is NASM for the
  x64 RIDs, `.s` is GNU syntax for llvm-mc, and a qualifier before the extension (`x.win.asm`,
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
| `AsmProduceStaticLibrary` | `false` | also archive the static library next to a shared one. A referencing project then links the static one. |
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
| `EnableDefaultAsmItems` | `true` | adds `**/*.asm` and `**/*.s` as `AsmSource` and `**/*.inc` as `AsmInclude`, excluding `bin/`, `obj/` and the other `DefaultItemExcludes`. Set `false` to list them yourself. |
| `EnableDefaultNoneItems` | `true` (.NET's own switch) | adds every other file of the project folder as a `None` item, as a `.csproj` does, so IDEs show it: a linker script, a C header, a README. The asm items are taken out of `None`, so each file is one item. Set `false` to list `None` items yourself. |
| `DefineConstants` | empty, then the SDK appends its symbols | semicolon-separated `NAME` or `NAME=VALUE`: `-DNAME` for NASM, `--defsym NAME=1` (or `=VALUE`, integers only) for llvm-mc. After your project body the SDK appends `DEBUG` or `RELEASE` (the configuration name in upper case, with `-`, `.` and spaces as `_`, exactly like C#), `FREESTANDING` or `LIBC`, `WINDOWS`, `LINUX` or `MACOS` (plus `MUSL`), and `X64` or `ARM64`, so setting the property never drops them. `DisableImplicitConfigurationDefines=true` turns the first off, and `DisableImplicitFrameworkDefines=true` the others. C#'s `TRACE` is not added. The SDK also defines `ASMSDK_EXPORTS_FILE` with an `ExportsFile`, and `ASMSDK_EXECUTABLE` in a program. |
| `DebugSymbols` | `true` in Debug (from `Microsoft.NET.Sdk.props`), `false` otherwise | debug information: DWARF (`-g -F dwarf`, llvm-mc `-g`) on Linux and macOS; CodeView (`-g -F cv8`) and a PDB on Windows. Set `true` for it in Release. Unlike .NET, whose Release builds still write a PDB, a Release build stays free of debug information by default. |
| `DebugType` | `portable` (from `Microsoft.NET.Sdk.props`) | `none` turns debug information off even when `DebugSymbols` is `true`. Other values change nothing. |
| `AsmFlags` | empty | extra NASM arguments. They are placed before `-I` and `-D`. Recommended: `-Xvc`, which makes NASM messages MSBuild diagnostics. |
| `AsmLlvmMcFlags` | empty | extra llvm-mc arguments, for `.s` sources. |
| `TreatWarningsAsErrors` | `false` | adds `-w+error` to NASM. NASM 3 warns `implicit DEFAULT ABS is deprecated` on any memory operand in a file without `default rel` or `default abs`. With this property, that warning fails the build. |
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
| `AsmPackSources` | `false` | also pack `AsmSource` and `AsmInclude` items into the package's `src/`, keeping their relative paths. |
| `AsmPackageLinkKind` | `Static` | what a consuming `.asmproj` links: `Static` takes the package's static library when it has one, otherwise its shared one (on Windows, the DLL's import library). `Shared` takes the shared one when it has one, otherwise the static one. |
| `DisableAsmConsumerTargets` | `false` | `true` drops the implicit `PackageReference` to `AsmFoundation.Sdk` that brings the consumer targets to referencing .NET projects. They then get no `runtimes/` layout and no `deps.json` entries. |
| `PackageId`, `Version`, `Authors`, `Description`, `PackageLicenseExpression`, `PackageReadmeFile`, … | as for any NuGet package | standard NuGet metadata. `NU5128` is suppressed on purpose. |

**Package layout.** `<tfm>` is the target framework, `<nuget-tfm>` the NuGet framework behind
it, and `<rid>` each RID the project builds. `dotnet pack` builds them all:

| Path | What |
|---|---|
| `include/**` | the `AsmPublicInclude` files, once, whatever the number of target frameworks |
| `src/**` | the sources, with `AsmPackSources` |
| `lib/<nuget-tfm>/_._` | an empty marker per target framework. NuGet judges compatibility by `lib/`: without it, a package with no `lib/` counts as compatible with every framework, and a freestanding project would restore a libc-only package, import nothing and fail at link time. The marker adds no assembly reference to a .NET consumer. |
| `build/<nuget-tfm>/<id>.props`, `buildTransitive/<nuget-tfm>/<id>.props` | generated per target framework: add the include folder and the library of the consuming `.asmproj`'s RID, by the RID graph's fallbacks, or stop with `ASM0060` when the package has none. NuGet imports the one nearest to the consumer's framework. |
| `native/<rid>/<tfm>/` | the static library (`lib<name>.a`, `<name>.lib`), and a Windows DLL's import library (`<name>.dll.lib`): for asm consumers |
| `runtimes/<rid>/native/` | the shared library of a single-target project (`<name>.dll`, `lib<name>.so`, `lib<name>.dylib`). .NET consumers get it copied and found by `[DllImport]`. |
| `runtimes/<rid>/nativeassets/<nuget-tfm>/` | the shared library of each target framework of a multi-targeted project (the file names would collide in `native/`). NuGet gives a .NET consumer the nearest framework's, which is `libc`'s. |

The implicit `AsmFoundation.Sdk` dependency is removed from the `.nuspec`: a package's
consumers need nothing from the SDK.

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
| `RuntimeIdentifiers` | all seven RIDs | restore writes a target per RID listed, and an inner build for a RID restore skipped fails with `NETSDK1047`. The project's own list was read first |
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
| `AsmSource` | `**/*.asm` and `**/*.s` | the sources. For each RID, the SDK picks one file per name by extension and qualifier (see [Runtime identifiers](#runtime-identifiers)), and runs NASM or llvm-mc once on it. An `.asm` that another file `%include`s is a source too, which gives `duplicate symbol`. Rename it `.inc`, or `<AsmSource Remove="x.asm" />`. Listing a source the default already includes is `ASM0024`. |
| `AsmInclude` | `**/*.inc` | build dependencies: editing one reassembles every source. Also packed under `src/` with `AsmPackSources`. **List include files from folders outside the project here too**, or edits to them will not rebuild. |
| `AsmIncludeDirectory` | `include/` (when it exists), then the project folder; the SDK's `include/` last | the `-I` paths. Items without `AsmOrigin` come first, in the order added; items from packages and references after; the SDK's own folder (`asmsdk.inc`, `asmsdk-gnu.inc`) last, so a project file of the same name wins. NASM tries the working directory, which is the project folder, before any of them. |
| `LinkerArg` | none | extra linker arguments, NativeAOT's item name, appended to every link line in order: `<LinkerArg Include="-z defs" />`. One item may hold several words; they are not quoted, so a path with spaces must carry its own quotes. **Footguns:** NativeAOT hands `LinkerArg` to clang (`-Wl,…`), and the SDK runs the linker directly, so write the linker's own syntax; and each OS's linker has its own (ld.lld: `-z defs`; lld-link: `-opt:ref`; ld64.lld: `-dead_strip`), so condition an item on `$(RuntimeIdentifier)`. |
| `AsmLinkLibrary` | none | extra link inputs (`.a`, `.lib`, `.so`, `.dylib`, `.o`), in order, after your objects. References and packages add theirs here. |
| `AsmLibcSymbol` | none | extra C function names for the C library stand-in (`TargetFramework=libc`): the Linux stub, `libSystem.tbd` (the underscore is added) or the UCRT import library (the name must be a `ucrtbase.dll` export). A program fails with `undefined symbol` until the function is listed; so does a Windows DLL or a dylib. A Linux shared library links anyway, and the function binds at load time. |
| `AsmPublicIncludeDirectory` | `include/` when it exists, else the project folder | what a referencing `.asmproj` adds to its `-I` path. Declaring any item replaces the default. |
| `AsmPublicInclude` | `include/**/*`, only when `include/` exists | the files packed into the package's `include/`. **Without an `include/` folder, the default is empty** and the package has no include files. Declaring any item replaces the default; packed paths are `include/%(RecursiveDir)%(Filename)%(Extension)`. |
| `PackageDownload` | `AsmFoundation.Toolchain.<host-rid>` at `[<version>]` | added when `AsmToolchain=Bundled`, `AsmToolchainDirectory` is empty, `AsmToolchainVersion` is set and the build machine has a package. A `PackageDownload` is not a dependency of your package. |
| `None` | every other file of the project folder (`EnableDefaultNoneItems`) | what IDEs show besides the sources and includes. The asm items are removed from it, so a file is never both. |
| `AvailableItemName` | `AsmSource`, `AsmInclude` | the item types an IDE shows besides its built-in ones, and offers as build actions. Rider's MSBuild host and Visual Studio's project system read it; without it an `.asmproj` looks empty. |
| `UpToDateCheckInput`, `UpToDateCheckBuilt` | the `AsmSource` and `AsmInclude` items and the `ExportsFile`; the output of each RID (unless `Objects`) | what the fast up-to-date checks of Visual Studio and Rider compare, so that editing a source makes the IDE build. `Visible="false"`: they name files that are items already. |

**Metadata the SDK reads or writes:**

| Metadata | On | Values |
|---|---|---|
| `AsmOrigin` | `AsmIncludeDirectory`, `AsmLinkLibrary` | `Package`, `Project` or `Sdk`. A directory with any `AsmOrigin` is ordered after local ones, and its `**/*.inc` become build dependencies. |
| `AsmPackage` | the same | the package id that contributed the item |
| `AsmProject` | the same | the referenced project's name |
| `AsmItemType` | items returned by `GetAsmLinkInputs` | `IncludeDirectory` or `LinkInput` |
| `ObjectFile`, `ObjectToolPath`, `SourceToolPath` | the sources selected for the RID, computed by `AsmPrepare` | `$(AsmIntermediateDirectory)obj/%(RecursiveDir)%(Filename).o` (the qualifier stays in the name: `io.linux.o`), and the absolute forward-slash paths handed to the assembler |
| `RuntimeIdentifiers` | a `ProjectReference` to an `.asmproj`, in a .NET project | the RIDs a portable build of the .NET project carries (passed on as `AsmRequestedRuntimeIdentifiers`) |

## Targets

| Target | Runs | Notes |
|---|---|---|
| `_AsmCheckProject` | before restore takes package references, and before the build's configuration check | raises `ASM0003` for an unknown target framework, and `ASM0100` to `ASM0108` for settings removed in 0.2.0 |
| `_AsmCheckRuntimeIdentifiers` | before the RID outer build dispatches, and before an inner build prepares | raises `ASM0010`, `ASM0011` and `ASM0012`; in a solution build, a project with no RID builds nothing with a message |
| `_AsmBuildEachRuntimeIdentifier` | the RID outer build's `Build` | builds the project once per RID, with `RuntimeIdentifier` as a global property |
| `_AsmCleanEachRuntimeIdentifier` | after `Clean`, in the outer build of each framework | cleans each RID's inner build |
| `_AsmResolveToolchain` | before `AsmPrepare` | locates the tools, raises `ASM0001`, `ASM0002` and `ASM0005`, and runs `chmod +x` on the bundled tools on Linux and macOS (NuGet drops the execute bit) |
| `AsmPrepare` | before assembling, linking and packing | selects the sources of the RID (`AsmSelectSources`: `ASM0020` to `ASM0024`), computes object paths, the ordered include directories, dependencies, defines, link inputs, and the `ExportsFile` and `LinkerArg` arguments; depends on `_AsmResolveProjectReferences` |
| `AsmAssemble` | once per out-of-date source (target batching) | NASM or llvm-mc (`ASM0004` without llvm-mc). Inputs: the source, every dependency `.inc`, the project file, the SDK's targets. Output: the object. A failed run deletes the object and raises `ASM0007` |
| `_AsmBuildSystemLibraries` | linked outputs that use the C library, and every Windows one | writes the stand-ins from the SDK's symbol lists: the Linux stub and `libasmsdk_nonshared.a`, `libSystem.tbd`, the `kernel32.lib` and `ucrtbase.lib` import libraries and `asmsdk_winstdio.lib` |
| `_AsmBuildCrt` | `libc` executables with no `StartupObject`, on Linux and Windows | `stubs/crt1.asm` or `crt1-arm64.s` → `crt1.o`; `stubs/wincrt-<arch>` → `wincrt.obj` |
| `AsmLink` | unless `Objects` | links (`ASM0015` for a macOS freestanding program), then checks a Windows or macOS image (`AsmCheckImage`: `ASM0080` to `ASM0082`). Inputs: objects, link inputs, the `ExportsFile`, the project file. Outputs: `$(TargetPath)` and the static library |
| `CoreCompile` | replaced, skipped in design-time builds | `AsmAssemble;AsmLink` in an inner build, the RID dispatch in the outer one; nothing when `DesignTimeBuild` or `SkipCompilerExecution` is `true`: IDEs call `Compile` and `CoreCompile` with both on every project load, and a design-time build must not run the tools (see [IDEs](guide/ides.md)) |
| `CreateManifestResourceNames` | replaced, empty | no resources |
| `CopyFilesToOutputDirectory` | replaced | nothing managed to copy; prints `<Project> -> <output>` once per RID |
| `GetTargetPathWithTargetPlatformMoniker` | replaced, returns nothing | so a referencing `.csproj` adds no assembly reference (a native file would raise `MSB3246`) |
| `GetAsmLinkInputs` | called by referencing `.asmproj` projects | returns the public include directories, then the static library, else the shared one (a DLL's import library), else (for `Objects`) the objects' absolute paths |
| `_AsmResolveProjectReferences` | in the referencing project | calls `GetAsmLinkInputs` on each referenced `.asmproj`, for this build's RID and the target framework NuGet chose; references to other project types are left alone |
| `_AsmSkipAsmReferencesInRidOuterBuild` | in the RID outer build | keeps the outer build from building its asm references with no RID: each inner build resolves them for its own |
| `GetAsmRuntimeIdentifiers`, `GetAsmConsumerInfo` | called by the consumer targets | the project's own RID list; its output kind and RIDs, asked without a RID |
| `GetAsmNativeRuntimeAssets` | called by the consumer targets and the copy targets | each RID's shared library (and PDB) with `runtimes/<rid>/native/` as its relative path |
| `GetCopyToOutputDirectoryItems`, `GetCopyToPublishDirectoryItems` | called by a referencing `.csproj` | the shared library: flat in a RID-specific build, under `runtimes/<rid>/native/` in a portable one |
| `_AsmComputePackLayout`, `_AsmGetRidPackContent`, `_AsmPackNativeContent` | `dotnet pack`, once per target framework (`TargetsForTfmSpecificContentInPackage`) | the package paths per RID, the consumer props, the `_._` marker; maps include files, sources, libraries and props into the package |
| `_AsmRemoveSdkPackageDependency` | after `GenerateNuspec` | removes the implicit `AsmFoundation.Sdk` dependency from the `.nuspec` (`ASM0050` if it cannot) |

**In a referencing .NET project** (`buildTransitive/AsmFoundation.Sdk.targets`):

| Target | Runs | Notes |
|---|---|---|
| `_AsmConfigureAsmProjectReferences` | after `_GetProjectReferenceTargetFrameworkProperties` | per asm reference: drops a non-shared-library reference with a message; a RID-specific build gets the RID that serves it best (`AsmSelectRuntimeIdentifier`, `ASM0040`, `ASM0041`); a portable build passes the reference's `RuntimeIdentifiers` metadata on; the app's publish properties (`SelfContained`, `PublishAot`, …) and output folder (`OutputPath`, `OutDir`) are not passed; a RID-specific build also asks for the reference's portable paths (`GetAsmNativeRuntimeAssets` without its RID) |
| `_AsmCollectNativeRuntimeAssets`, `_AsmAddNativeAssetsToBuildDepsFile`, `_AsmAddNativeAssetsToPublishDepsFile` | after the `deps.json` is generated, for build and publish | add each library as a `runtimeTargets` entry of a project library, so the .NET host finds the right one (`ASM0042` if the file cannot be read) |
| `_AsmDropPortableNativeCopies` | after `GetCopyToOutputDirectoryItems`, RID-specific builds | removes the copies headed for the asm references' `runtimes/<rid>/native/` paths, which a RID-agnostic class library in between hands on; matched by destination, so the flat copy of the same file stays |
| `_AsmPrepareNativePublishAssets` | after `ComputeResolvedFilesToPublishList` | the same for publish (RID-specific), and `AssetType=native` on each asm library, which the .NET SDK's single-file step decides by (beside the bundle, or inside it with `IncludeNativeLibrariesForSelfExtract`) |

## Files the build writes

| Path | What |
|---|---|
| `bin/<Configuration>/<tfm>/<rid>/` | the outputs: the library or program, a Windows DLL's `<name>.dll.lib`, a Windows program's `<name>.lib` when its `ExportsFile` exports, the `.pdb` of a Windows debug build |
| `obj/<Configuration>/<tfm>/<rid>/asm/obj/<subfolder>/<file>.o` | one object per selected source |
| `obj/<Configuration>/<tfm>/<rid>/asm/stubs/` | the stand-ins and startup code of this RID: `libc-stub.asm` or `.s`, `libc.so.6` or `libc.musl-x86_64.so.1`, `libasmsdk_nonshared.a`, `crt1.o` (Linux); `libSystem.tbd` (macOS); `kernel32.def`, `kernel32.lib`, `ucrtbase.def`, `ucrtbase.lib`, `asmsdk_winstdio.lib`, `wincrt.obj` (Windows) |
| `obj/<Configuration>/<tfm>/asm/pack/<PackageId>.props`, `_._` | the consumer props and the `lib/` marker for the package |
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
| `ASM0010` | No RID to build: none listed, none asked for, no `-r`. | `RuntimeIdentifiers`, a `ProjectReference`'s `RuntimeIdentifiers`, or `-r`. |
| `ASM0011` | A RID outside the seven. | One of the seven. |
| `ASM0012` | A RID asked for that the project does not list. | Add it, if the code supports it. |
| `ASM0015` | A freestanding program for macOS. | `TargetFramework=libc`, or a library. |
| `ASM0020` | Two files of one name apply equally to a RID. | Keep one, or qualify one more narrowly. |
| `ASM0021` | A `.asm` file qualified for ARM64 only. | ARM64 code goes in `.s` files. |
| `ASM0022` | The source selection was asked for an unknown RID. | An internal check: report it. |
| `ASM0023` | No source builds a listed RID. | Add a source for it (ARM64: a `.s` file), or remove the RID. |
| `ASM0024` | One source listed twice as `AsmSource`. | Remove the explicit item, or `EnableDefaultAsmItems=false`. |
| `ASM0030` | The `ExportsFile` cannot be read, has an `extern "C++"` block, or a pattern on Windows. | List exact names. |
| `ASM0040` | A RID-specific .NET build references an asm project none of whose RIDs serves its RID. | Add a serving RID (the message lists them). |
| `ASM0041`, `ASM0042` | The .NET SDK's RID graph, or the `deps.json`, cannot be read. | Repair the .NET SDK; rebuild. |
| `ASM0050` | Pack cannot remove the SDK dependency from the `.nuspec`. | Report it. |
| `ASM0060` | A package has no build for the consuming asm project's RID. | Build for a RID the message lists, or use a package packed for yours. |
| `ASM0061` | The consumer props of a package cannot be written. | Check the `obj/` folder's permissions. |
| `ASM0070` | A C library stand-in cannot be written. | Check the `obj/` folder's permissions; report it otherwise. |
| `ASM0080` | A 64-bit Windows image needs a 32-bit absolute address fixup, which cannot work above 4 GB. The image was deleted. | `default rel`; `dq` in data tables. |
| `ASM0081` | A macOS image needs a fixup in a read-only segment (a text relocation). The image was deleted. | `lea rax, [rel label]`, `ADDRESS`; pointer tables in `.data`. |
| `ASM0082` | The linked image could not be read or parsed for the check. | Rebuild; report it if it persists. |
| `ASM0100` to `ASM0107` | A setting of AsmFoundation.Sdk 0.1.0 that 0.2.0 replaced. Each error names its replacement: `AsmLibraryName` → `AssemblyName`, `AsmRuntime` → `TargetFramework`, `AsmDefineConstants` and `AsmDefine` items → `DefineConstants`, `AsmDebugInfo` → `DebugSymbols`, `AsmEntryPoint` → `StartupObject`, `AsmLinkFlags` → `LinkerArg` items and `ExportsFile`, `AsmOutputKind` → `OutputType`. | Make the change the error names. One build lists every removed setting. |
| `ASM0108` | The summary that stops the build after `ASM0100` to `ASM0107`. | Fix those. |
| `NU1201` | A project references an `.asmproj` whose target frameworks it cannot use: a `freestanding` project and a `libc`-only reference. | Make the reference target `freestanding` too (or both), or make this project `libc`. |
| `NU1202` | The same for a package. | The same, for the package's project. |
| `MSB3073 … exited with code 1` | The linker, or a stand-in's tool, failed. | Read the line above it. Add `<AsmFlags>-Xvc</AsmFlags>` so NASM's messages become MSBuild errors with file and line. |
| `MSB4236` / SDK not found | `global.json` or `nuget.config` does not lead to `AsmFoundation.Sdk`. | Check `msbuild-sdks` and the feeds (see [The toolchain](toolchain/index.md)). |
| `MSB4025 … ProjectType '' not found` | `.slnx` entry without a type. | Add `Type="13B669BE-BB05-4DDF-9536-439F39A36129"`. |

[Troubleshooting](guide/troubleshooting.md) has the full messages, and the NASM and linker
errors.
