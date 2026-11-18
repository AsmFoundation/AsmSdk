# `.asmproj` reference

Everything AsmFoundation.Sdk reads from a project, sets, runs and writes, with the exact
defaults of the 0.2.0 targets. The guides explain when to use each setting. Start at
[What is AsmSdk](guide/index.md).

- **Properties** go in a `<PropertyGroup>`, and **items** in an `<ItemGroup>`, as in any
  SDK-style project.
- **A setting that has a .NET name uses it:** `TargetFramework`, `OutputType`,
  `AssemblyName`, `DefineConstants`, `DebugSymbols`, `DebugType`, `StartupObject`, and
  NativeAOT's `LinkerArg` and `ExportsFile`. `Asm*` names exist only where .NET has no
  counterpart: the toolchain, packing, and the assembler and linker details.
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
      `AsmFoundationSdkVersion=0.2.0` and `AsmFoundationDefaultToolchainVersion=0.1.0`.
   2. `targets/AsmFoundation.Properties.targets`. **Every SDK default is applied here,
      after your project body.** A default applies only when you left the property empty.
      This file also maps the target framework to a NuGet framework, and hides
      `OutputType`, `DebugType`, `DebugSymbols` and `DisableImplicitFrameworkDefines` from
      the managed SDK (see [Properties the SDK sets for you](#properties-the-sdk-sets-for-you)).
   3. `Microsoft.NET.Sdk`'s `Sdk.targets`: restore, pack, references and Build.
   4. `targets/AsmFoundation.Build.targets`, `targets/AsmFoundation.References.targets` and
      `targets/AsmFoundation.Pack.targets`. These replace managed targets of the same name.

**Consequences:**

- A project that reads an SDK default sees it empty. `$(DefineConstants)` inside your project
  does not contain `DEBUG` yet: like C#, the SDK appends `DEBUG` (or `RELEASE`) and the target
  framework symbol after the body. So `<DefineConstants>FOO</DefineConstants>` keeps them, and
  so does `<DefineConstants>$(DefineConstants);FOO</DefineConstants>`.
- After `Properties.targets`, `OutputType` is always `Library`, `DebugType` is `none` and
  `DebugSymbols` is `false`, whatever you set: the SDK has read your values into internal
  properties first. A target of your own that runs later sees the hidden values.

## Target frameworks

The target framework says what the code links against, the way `net10.0` says which .NET it
runs on.

| `TargetFramework` | The code | Executable | Shared library | Runs on |
|---|---|---|---|---|
| `freestanding` (the default) | makes its own system calls | static, entered at `_start` | no `DT_NEEDED` | any x86-64 Linux kernel |
| `libc` | links the C library | PIE, entered at `main` through the SDK's `crt1` | `DT_NEEDED libc.so.6` | glibc 2.34 or later |

- **`TargetFrameworks`** builds the project once per name, exactly like a multi-targeted
  `.csproj`: `<TargetFrameworks>freestanding;libc</TargetFrameworks>`. Every build has its
  own `bin/<Configuration>/<tfm>/` and `obj/<Configuration>/<tfm>/` folders.
- **No `TargetFramework`** means `freestanding`. The default is applied after the project
  body, so a project's own `TargetFrameworks` still multi-targets.
- **Any other name fails** with `ASM0003`, including `netstandard2.0`, which was the hidden
  default of 0.1.0.
- **Preprocessor symbol:** NASM gets `-DFREESTANDING` or `-DLIBC`, the counterpart of a
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
  code that calls glibc.
- **A project that targets both is referenced by its nearest framework.** A `libc` project
  gets the `libc` build, and a `freestanding` project gets the `freestanding` build.
- **Any `.csproj` can reference any `.asmproj`,** because every .NET implementation accepts
  both frameworks. A .NET project gets the `libc` build of a project that targets both.
- **The two names need two frameworks.** With one framework behind both, `dotnet pack` fails
  with `NU5051` and a `.csproj` reference with `NU1201` (measured with .NET SDK 11.0.100).

## Output properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `OutputType` | `Library` | `Library` gives a shared `.so`, `StaticLibrary` a `.a`, `Exe` or `WinExe` an ELF executable, and `Objects` object files only. Any other value builds a shared library. The SDK reads it into its internal `_AsmOutputKind` (`SharedLibrary`, `StaticLibrary`, `Executable` or `Objects`) and then **sets `OutputType` back to `Library`**, so the managed SDK never builds an apphost or a runtimeconfig. A target of your own can read `_AsmOutputKind`, but it is internal and may change. |
| `AssemblyName` | the project name | the base name: `lib<name>.so`, `lib<name>.a`, or `<name>` for an executable. Libraries get the `lib` prefix because `-l<name>` and .NET's `[DllImport("<name>")]` both look for it on Linux. The packed `.a` is named after it too, whatever the `PackageId`. |
| `AsmProduceStaticLibrary` | `false` | also archive `lib<name>.a` next to a shared library. A referencing project then links the `.a`. |
| `TargetName` | `lib$(AssemblyName)` for libraries, `$(AssemblyName)` for an executable | set only when empty. Prefer `AssemblyName`. |
| `TargetFileName` | `$(TargetName).so`, `$(TargetName).a`, or `$(TargetName)` | set explicitly, so that the common targets never append `.dll`. |
| `AsmTargetRid` | `linux-x64` | where the code runs. Only `linux-x64` exists. It names the package folders (`native/linux-x64/`, `runtimes/linux-x64/`). |
| `AsmFormat` | `elf64` | the NASM `-f` value for your sources. The stubs always use `elf64`. |

Outputs go to `bin/<Configuration>/<TargetFramework>/`, as in .NET
(`AppendTargetFrameworkToOutputPath` keeps its default, `true`).

## Runtime and link properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `TargetFramework` | `freestanding` | see [Target frameworks](#target-frameworks). `libc` links a generated `libc.so.6` stub into a shared library or an executable (`DT_NEEDED libc.so.6`). A `StaticLibrary` or `Objects` project links nothing, but its target framework still decides who may reference it. |
| `StartupObject` | empty, which means `_start` | the executable's entry symbol (`-e`), .NET's "where the program starts". On `libc`, leave it empty to get the SDK's `crt1` (`_start` → `__libc_start_main` → `main`). Setting it on `libc` skips `crt1`, and your entry must then do what `crt1` does. |
| `AsmDynamicLinker` | `/lib64/ld-linux-x86-64.so.2` | `PT_INTERP` of a `libc` executable. |
| `ExportsFile` | empty | NativeAOT's name for the linker version script: `--version-script=<file>`, with a relative path taken from the project folder. Only the symbols it lists as global stay in the dynamic symbol table, versioned by its version node. For an executable the SDK also passes `--export-dynamic`, as NativeAOT does. Editing the file relinks. |
| `AsmSharedLibraryPath` | `$(TargetPath)` for a shared library | computed. Overwritten even when you set it. |
| `AsmStaticLibraryPath` | `$(TargetPath)` for a `StaticLibrary`; `$(TargetDir)lib$(AssemblyName).a` with `AsmProduceStaticLibrary` | where the archive goes. Set only when empty. |

Extra linker arguments are `LinkerArg` items (see [Items](#items-and-their-metadata)).

### Link lines

Every link ends with `-z noexecstack --build-id=sha1`, then the `ExportsFile` arguments, then
the `LinkerArg` items, so your own arguments come last.

| Output | Command |
|---|---|
| Shared library | `-shared -soname lib<name>.so -z relro -z now --hash-style=gnu`, then the objects, the `AsmLinkLibrary` inputs and, on `libc`, `--no-as-needed libc.so.6` (the stub) |
| Freestanding executable | `-static -e <StartupObject or _start>`, then the objects and inputs |
| libc executable | `-pie --dynamic-linker /lib64/ld-linux-x86-64.so.2 -z relro -z now --hash-style=gnu -e _start crt1.o`, then the objects, the inputs and the stub |
| Static library | the archive is deleted, then `llvm-ar rcsD lib<name>.a <objects>`, so that members of removed sources do not linger |

The build id is derived from the content, and `D` keeps the archive free of timestamps, so
the same inputs give the same outputs. The inputs include each source's absolute path, which
NASM records in the symbol table.

### The libc stub

For `TargetFramework=libc`, the SDK assembles `stubs/libc.asm` and links it as a shared
object named `libc.so.6`:

- **What it contains:** about 290 libc functions as empty bodies, plus four pointer-sized
  data objects: `stdin`, `stdout`, `stderr` and `environ`.
- **What linking against it gives:** `DT_NEEDED libc.so.6`, and the PLT and GOT entries.
  At run time, glibc supplies every symbol.
- **Symbol versions:** the stub's symbols are unversioned, so references bind to glibc's
  default versions, the ones a C program compiled today uses.
- **`pthread_*` and `dl*`** are listed as `libc.so.6` symbols, which they are since
  glibc 2.34. That is why libc programs need glibc 2.34 or later.
- **A function the stub does not list:**
  - an **executable** fails with `undefined symbol: <name>` until you add
    `<AsmLibcSymbol Include="<name>" />`;
  - a **shared library** links anyway, and glibc supplies the function when the library
    loads, unless you link with `<LinkerArg Include="-z defs" />`.

## Assembly properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `EnableDefaultAsmItems` | `true` | adds `**/*.asm` as `AsmSource` and `**/*.inc` as `AsmInclude`, excluding `bin/`, `obj/` and the other `DefaultItemExcludes`. Set `false` to list them yourself. |
| `DefineConstants` | empty, then the SDK appends the configuration and target framework symbols | semicolon-separated `NAME` or `NAME=VALUE`, each passed as `-DNAME` or `-DNAME=VALUE`. The SDK appends `DEBUG` or `RELEASE` (the configuration name in upper case, with `-`, `.` and spaces as `_`, exactly like C#) and `FREESTANDING` or `LIBC` after your project body, so setting the property never drops them. `DisableImplicitConfigurationDefines=true` and `DisableImplicitFrameworkDefines=true` turn them off. C#'s `TRACE` is not added. |
| `DebugSymbols` | `true` in Debug (from `Microsoft.NET.Sdk.props`), `false` otherwise | `-g -F dwarf`: DWARF in the objects and in the linked output. Set `true` for DWARF in Release. Unlike .NET, whose Release builds still write a PDB, a Release build stays free of debug information by default. |
| `DebugType` | `portable` (from `Microsoft.NET.Sdk.props`) | `none` turns DWARF off even when `DebugSymbols` is `true`. Other values do not change the DWARF. |
| `AsmFlags` | empty | extra NASM arguments. They are placed before `-I` and `-D`. Recommended: `-Xvc`, which makes NASM messages MSBuild diagnostics. |
| `TreatWarningsAsErrors` | `false` | adds `-w+error`. NASM 3 warns `implicit DEFAULT ABS is deprecated` on any memory operand in a file without `default rel` or `default abs`. With this property, that warning fails the build. |
| `AsmIntermediateDirectory` | `$(IntermediateOutputPath)asm/` (`obj/<Configuration>/<tfm>/asm/`) | objects in `obj/`, stubs in `stubs/`, generated package files in `pack/`. |

**The warning set is fixed:** `-w+all -w-reloc-rel-dword -w-reloc-abs-qword`.

- The two exceptions fire on relocations that NASM cannot judge. The first is a RIP-relative
  reference to another section, which position-independent code needs and which always
  links. The second is a `dq label`: valid in a writable section, or anywhere in a
  freestanding executable, and rejected by the linker in read-only data of a shared library
  or a PIE.
- Add `-w-<name>` to `AsmFlags` to silence another warning, for example
  `-w-label-orphan`. The name is printed in brackets after each warning.
- The SDK's own stubs are always assembled with `-w+error`.

**The NASM command line, as run** (from the project folder, with forward-slash paths):

```text
nasm -f $(AsmFormat) [-g -F dwarf] -w+all -w-reloc-rel-dword -w-reloc-abs-qword [-w+error] $(AsmFlags)
     -I"<dir>/" ... -D<your symbols> ... -DDEBUG|-DRELEASE -DFREESTANDING|-DLIBC
     -o "<obj>/x.o" "<project>/x.asm"
```

## Sharing and pack properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmPackSources` | `false` | also pack `AsmSource` and `AsmInclude` items into the package's `src/`, keeping their relative paths. |
| `AsmPackageLinkKind` | `Static` | what a consuming `.asmproj` links: `Static` takes the package's `.a` when it has one, otherwise its `.so`. `Shared` takes the `.so` when it has one, otherwise its `.a`. |
| `PackageId`, `Version`, `Authors`, `Description`, `PackageLicenseExpression`, `PackageReadmeFile`, … | as for any NuGet package | standard NuGet metadata. `NU5128` is suppressed on purpose. |

**Package layout.** `<tfm>` is the target framework and `<nuget-tfm>` the NuGet framework
behind it:

| Path | What |
|---|---|
| `include/**` | the `AsmPublicInclude` files, once, whatever the number of target frameworks |
| `src/**` | the sources, with `AsmPackSources` |
| `lib/<nuget-tfm>/_._` | an empty marker per target framework. NuGet judges compatibility by `lib/`: without it, a package with no `lib/` counts as compatible with every framework, and a freestanding project would restore a libc-only package, import nothing and fail at link time. The marker adds no assembly reference to a .NET consumer. |
| `build/<nuget-tfm>/<id>.props`, `buildTransitive/<nuget-tfm>/<id>.props` | generated per target framework: add the include folder and that framework's library to a consuming `.asmproj`. NuGet imports the one nearest to the consumer's framework. |
| `native/linux-x64/<tfm>/lib<name>.a` | the static library |
| `runtimes/linux-x64/native/lib<name>.so` | the shared library of a single-target project. .NET consumers get it copied and found by `[DllImport]`. |
| `runtimes/linux-x64/nativeassets/<nuget-tfm>/lib<name>.so` | the shared library of each target framework of a multi-targeted project (the file names would collide in `native/`). NuGet gives a .NET consumer the nearest framework's, which is `libc`'s. |

## Toolchain properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmToolchain` | `Bundled` | `Bundled`: NASM, `lld` (run as `-flavor gnu`) and `llvm-ar` from the toolchain package. `System`: `nasm`, `ld` and `ar` from PATH. Any other value fails with `ASM0002`. |
| `AsmToolchainVersion` | `$(AsmFoundationDefaultToolchainVersion)`, which is `0.1.0` for SDK 0.2.0 | the toolchain package version to download. When it is empty, no download is added. |
| `AsmHostRid` | from `NETCoreSdkRuntimeIdentifier`: `win-<arch>`, `osx-<arch>` or `linux-<arch>` | the build machine. Distribution RIDs (`ubuntu.24.04-x64`) become portable ones. |
| `AsmToolchainPackageId` | `AsmFoundation.Toolchain.$(AsmHostRid)` | packages exist for `win-x64`, `linux-x64` and `osx-arm64` only. |
| `AsmToolchainDirectory` | `<NuGetPackageRoot>/asmfoundation.toolchain.<rid>/<version>/tools/` | set it to use tools from another folder: no download happens, and the folder must hold `nasm`, `lld` and `llvm-ar` (`.exe` on Windows). |
| `AsmNasmCommand`, `AsmLinkerCommand`, `AsmArchiverCommand` | `nasm`, `ld`, `ar` | the commands used with `AsmToolchain=System`. They may include arguments. |

## Properties the SDK sets for you

Read-only in practice.

**In `Sdk.props`, before the project body:**

| Property | Value | Why |
|---|---|---|
| `UsingAsmFoundationSdk` | `true` | detect asm projects in `Directory.Build.*`, targets and package props |
| `AsmFoundationSdkDirectory` | the SDK package root | where `Sdk/`, `targets/` and `stubs/` live |
| `EnableDefaultItems` | `false` | no `Compile`, `None` or `EmbeddedResource` globs |
| `DisableImplicitFrameworkReferences`, `AutomaticallyUseReferenceAssemblyPackages` | `true`, `false` | no NETStandard.Library reference |
| `GenerateAssemblyInfo`, `ProduceReferenceAssembly`, `GenerateDependencyFile`, `GenerateRuntimeConfigurationFiles`, `UseAppHost` | `false` | nothing managed exists |
| `IncludeBuildOutput`, `CopyBuildOutputToOutputDirectory`, `CopyOutputSymbolsToOutputDirectory` | `false` | the native outputs are written and packed by the SDK's own steps |
| `SkipCopyBuildProduct` | `true` | the same reason |
| `NoWarn` | `+NU5128` | a native package's payload is outside `lib/` by design |

**In `Properties.targets`, after the project body:**

| Property | Value | Why |
|---|---|---|
| `TargetFramework` | `freestanding`, when neither `TargetFramework` nor `TargetFrameworks` is set | the default target framework |
| `TargetFrameworkIdentifier`, `TargetFrameworkVersion` | `.NETStandard`, `v1.0` (freestanding) or `v2.0` (libc) | the NuGet framework behind the alias; Microsoft.NET.Sdk then never parses the alias |
| `LanguageTargets` | `Microsoft.Common.CrossTargeting.targets`, in the outer build of a `TargetFrameworks` project | Microsoft.NET.Sdk gives an unknown project extension the single-target common targets even there, and the outer build fails |
| `CheckNotRecommendedTargetFramework` | `false` | no "netstandard1.x is not recommended" warning: nothing managed is built for it |
| `OutputType` | `Library` | hidden: the managed SDK would build an apphost for `Exe` |
| `DebugType`, `DebugSymbols` | `none`, `false` | hidden: the managed SDK would expect a PDB that nothing writes |
| `DisableImplicitFrameworkDefines` | `true` | hidden: the managed SDK would append `NETSTANDARD2_0` and the like to `DefineConstants` |

## Items and their metadata

| Item | Default | Meaning and footguns |
|---|---|---|
| `AsmSource` | `**/*.asm` | one NASM run per item. An `.asm` that another file `%include`s is a source too, which gives `duplicate symbol`. Rename it `.inc`, or `<AsmSource Remove="x.asm" />`. |
| `AsmInclude` | `**/*.inc` | build dependencies: editing one reassembles every source. Also packed under `src/` with `AsmPackSources`. **List include files from folders outside the project here too**, or edits to them will not rebuild. |
| `AsmIncludeDirectory` | `include/` (when it exists), then the project folder | the `-I` paths. Items without `AsmOrigin` come first, in the order added; items with `AsmOrigin` (from packages and references) come after. NASM tries the working directory, which is the project folder, before any of them. |
| `LinkerArg` | none | extra linker arguments, NativeAOT's item name, appended to every link line in order: `<LinkerArg Include="-z defs" />`. One item may hold several words; they are not quoted, so a path with spaces must carry its own quotes. **Footgun for NativeAOT users:** NativeAOT hands `LinkerArg` to clang (`-Wl,…`), and the SDK runs `ld.lld` directly, so write the linker's own syntax. |
| `AsmLinkLibrary` | none | extra link inputs (`.a`, `.so`, `.o`), in order, after your objects. References and packages add theirs here. |
| `AsmLibcSymbol` | none | extra function names for the `libc.so.6` stub (`TargetFramework=libc`). A libc executable fails with `undefined symbol` until the function is listed. A shared library links anyway, and the function binds at load time. |
| `AsmPublicIncludeDirectory` | `include/` when it exists, else the project folder | what a referencing `.asmproj` adds to its `-I` path. Declaring any item replaces the default. |
| `AsmPublicInclude` | `include/**/*`, only when `include/` exists | the files packed into the package's `include/`. **Without an `include/` folder, the default is empty** and the package has no include files. Declaring any item replaces the default; packed paths are `include/%(RecursiveDir)%(Filename)%(Extension)`. |
| `PackageDownload` | `AsmFoundation.Toolchain.<host-rid>` at `[<version>]` | added when `AsmToolchain=Bundled`, `AsmToolchainDirectory` is empty and `AsmToolchainVersion` is set. A `PackageDownload` is not a dependency of your package. |

**Metadata the SDK reads or writes:**

| Metadata | On | Values |
|---|---|---|
| `AsmOrigin` | `AsmIncludeDirectory`, `AsmLinkLibrary` | `Package` or `Project`. A directory with any `AsmOrigin` is ordered after local ones, and its `**/*.inc` become build dependencies. |
| `AsmPackage` | the same | the package id that contributed the item |
| `AsmProject` | the same | the referenced project's name |
| `AsmItemType` | items returned by `GetAsmLinkInputs` | `IncludeDirectory` or `LinkInput` |
| `ObjectFile`, `ObjectToolPath`, `SourceToolPath` | `AsmSource`, computed by `AsmPrepare` | `$(AsmIntermediateDirectory)obj/%(RecursiveDir)%(Filename).o`, and the absolute forward-slash paths handed to NASM |

## Targets

| Target | Runs | Notes |
|---|---|---|
| `_AsmCheckProject` | before restore takes package references, and before the build's configuration check | raises `ASM0003` for an unknown target framework, and `ASM0100` to `ASM0108` for settings removed in 0.2.0 |
| `_AsmResolveToolchain` | before `AsmPrepare` | locates the tools, raises `ASM0001`/`ASM0002`, and runs `chmod +x` on the bundled tools on Linux and macOS (NuGet drops the execute bit) |
| `AsmPrepare` | before assembling, linking and packing | computes object paths, the ordered include directories, dependencies, defines, link inputs and the `ExportsFile` and `LinkerArg` arguments; depends on `_AsmResolveProjectReferences` |
| `AsmAssemble` | once per out-of-date source (target batching) | Inputs: the source, every dependency `.inc`, the project file. Output: the object |
| `_AsmBuildLibcStub` | `libc` shared libraries and executables | `stubs/libc.asm` → `obj/…/asm/stubs/libc.so.6` (`-soname libc.so.6`) |
| `_AsmBuildCrt` | `libc` executables with no `StartupObject` | `stubs/crt1.asm` → `obj/…/asm/stubs/crt1.o` |
| `AsmLink` | unless `Objects` | Inputs: objects, link inputs, the `ExportsFile`, the project file. Outputs: `$(TargetPath)` and the static library |
| `CoreCompile` | replaced | `AsmAssemble;AsmLink` |
| `CreateManifestResourceNames`, `CopyFilesToOutputDirectory` | replaced, empty | no resources, nothing managed to copy |
| `GetTargetPathWithTargetPlatformMoniker` | replaced, returns nothing | so a referencing `.csproj` adds no assembly reference (a `.so` would raise `MSB3246`) |
| `GetAsmLinkInputs` | called by referencing `.asmproj` projects | returns the public include directories, then the static library, else the shared library, else (for `Objects`) the objects |
| `_AsmResolveProjectReferences` | in the referencing project | calls `GetAsmLinkInputs` on each referenced `.asmproj`, with the target framework NuGet chose and the same global properties `ResolveProjectReferences` uses; references to other project types are left alone |
| `GetCopyToOutputDirectoryItems`, `GetCopyToPublishDirectoryItems` | called by a referencing `.csproj` | return the `.so` (`PreserveNewest`), so it lands next to the program |
| `_AsmComputePackLayout`, `_AsmWriteConsumerProps` | `dotnet pack`, once per target framework | the package paths, and `obj/…/asm/pack/<PackageId>.props` plus the empty `_._` marker |
| `_AsmPackNativeContent` | `dotnet pack` (`TargetsForTfmSpecificContentInPackage`) | maps include files, sources, libraries, markers and props into the package layout |

## Files the build writes

| Path | What |
|---|---|
| `bin/<Configuration>/<tfm>/lib<name>.so`, `lib<name>.a`, `<name>` | the outputs |
| `obj/<Configuration>/<tfm>/asm/obj/<subfolder>/<file>.o` | one object per source |
| `obj/<Configuration>/<tfm>/asm/stubs/libc.o`, `libc.so.6`, `crt1.o` | the libc stub and start-up code |
| `obj/<Configuration>/<tfm>/asm/pack/<PackageId>.props`, `_._` | the consumer props and the `lib/` marker for the package |
| `bin/<Configuration>/<PackageId>.<Version>.nupkg` | `dotnet pack` output, unless `-o` or `PackageOutputPath` says otherwise |

The SDK registers every file in this table except the `.nupkg` in `FileWrites`, so
`dotnet clean` removes them. A file that your own target generates is removed by
`dotnet clean` only if you add it to `FileWrites` too.

## Errors and warnings

| Code or text | Meaning | Fix |
|---|---|---|
| `ASM0001` | The bundled toolchain is not where restore puts it. | Run `dotnet restore`; don't build with `--no-restore` on a clean machine; or use `AsmToolchain=System` or `AsmToolchainDirectory`. |
| `ASM0002` | `AsmToolchain` is neither `Bundled` nor `System`. | Use one of the two. |
| `ASM0003` | `TargetFramework` is neither `freestanding` nor `libc`. | Use one of the two, or `TargetFrameworks=freestanding;libc`. |
| `ASM0100` to `ASM0107` | A setting of AsmFoundation.Sdk 0.1.0 that 0.2.0 replaced. Each error names its replacement: `AsmLibraryName` → `AssemblyName`, `AsmRuntime` → `TargetFramework`, `AsmDefineConstants` and `AsmDefine` items → `DefineConstants`, `AsmDebugInfo` → `DebugSymbols`, `AsmEntryPoint` → `StartupObject`, `AsmLinkFlags` → `LinkerArg` items and `ExportsFile`, `AsmOutputKind` → `OutputType`. | Make the change the error names. One build lists every removed setting. |
| `ASM0108` | The summary that stops the build after `ASM0100` to `ASM0107`. | Fix those. |
| `NU1201` | A project references an `.asmproj` whose target frameworks it cannot use: a `freestanding` project and a `libc`-only reference. | Make the reference target `freestanding` too (or both), or make this project `libc`. |
| `NU1202` | The same for a package. | The same, for the package's project. |
| `MSB3073 … exited with code 1` | NASM or the linker failed. | Read the line above it. Add `<AsmFlags>-Xvc</AsmFlags>` so NASM's messages become MSBuild errors with file and line. |
| `NU1101 Unable to find package AsmFoundation.Toolchain.<rid>` | No toolchain package for this build machine or feed. | Use a supported host, add the feed, or use `AsmToolchain=System`. |
| `MSB4236` / SDK not found | `global.json` or `nuget.config` does not lead to `AsmFoundation.Sdk`. | Check `msbuild-sdks` and the feeds (see [The toolchain](toolchain/index.md)). |
| `MSB4025 … ProjectType '' not found` | `.slnx` entry without a type. | Add `Type="13B669BE-BB05-4DDF-9536-439F39A36129"`. |

[Troubleshooting](guide/troubleshooting.md) has the NASM and linker errors.
