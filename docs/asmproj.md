# `.asmproj` reference

Everything AsmFoundation.Sdk reads from a project, sets, runs and writes, with the exact
defaults of the 0.1.0 targets. The guides explain when to use each setting. Start at
[What is AsmSdk](guide/index.md).

- **Properties** go in a `<PropertyGroup>`, and **items** in an `<ItemGroup>`, as in any
  SDK-style project.
- **Anything not listed works as it does for a `.csproj`,** for example `Version`,
  `PackageId`, `Configuration`, `Directory.Build.props`, `Directory.Packages.props`,
  `nuget.config` and `global.json`.


## How a project is evaluated

MSBuild reads a project in this order, and the order explains most surprises:

1. **`Sdk/Sdk.props`** (AsmFoundation.Sdk) sets `UsingAsmFoundationSdk=true` and switches
   off the managed build. It then imports `Microsoft.NET.Sdk`'s `Sdk.props`, which reads
   `Directory.Build.props`. So `$(UsingAsmFoundationSdk)` is already `true` inside
   `Directory.Build.props`.
2. **The project body.** Your properties and items.
3. **`Sdk/Sdk.targets`.** It imports these files, in this order:
   1. `Sdk/AsmFoundation.Sdk.Version.props`, generated at pack time:
      `AsmFoundationSdkVersion=0.1.0` and `AsmFoundationDefaultToolchainVersion=0.1.0`.
   2. `targets/AsmFoundation.Properties.targets`. **Every SDK default is applied here,
      after your project body.** A default applies only when you left the property empty.
   3. `Microsoft.NET.Sdk`'s `Sdk.targets`: restore, pack, references and Build.
   4. `targets/AsmFoundation.Build.targets`, `targets/AsmFoundation.References.targets` and
      `targets/AsmFoundation.Pack.targets`. These replace managed targets of the same name.

**Consequence:** a project that reads an SDK default sees it empty. `$(AsmDefineConstants)`
inside your project is empty, not `DEBUG`. Any non-empty value you set wins over the
default, so `$(AsmDefineConstants);FOO` ends up as `;FOO`, without `DEBUG`.

## Output properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `OutputType` | `Library` | `Library` gives a shared `.so`, `StaticLibrary` a `.a`, `Exe` or `WinExe` an ELF executable, and `Objects` object files only. The SDK maps it to `AsmOutputKind` and then **sets `OutputType` back to `Library`**, so the managed SDK never builds an apphost or a runtimeconfig. Read `AsmOutputKind`, not `OutputType`, in your own targets. |
| `AsmOutputKind` | from `OutputType` | `SharedLibrary`, `StaticLibrary`, `Executable` or `Objects`. Set it to bypass `OutputType`. |
| `AsmLibraryName` | `$(AssemblyName)`, otherwise `$(MSBuildProjectName)` | the base name: `lib<name>.so`, `lib<name>.a`, or `<name>` for an executable. The packed `.a` is named after it too, whatever the `PackageId`. |
| `AsmProduceStaticLibrary` | `false` | also archive `lib<name>.a` next to a shared library. A referencing project then links the `.a`. |
| `TargetName` | `lib$(AsmLibraryName)` for libraries, `$(AsmLibraryName)` for an executable | set only when empty. Prefer `AsmLibraryName`. |
| `TargetFileName` | `$(TargetName).so`, `$(TargetName).a`, or `$(TargetName)` | set explicitly, so that the common targets never append `.dll`. |
| `AsmTargetRid` | `linux-x64` | where the code runs. Only `linux-x64` exists. It names the package folders (`native/linux-x64/`, `runtimes/linux-x64/native/`). |
| `AsmFormat` | `elf64` | the NASM `-f` value for your sources. The stubs always use `elf64`. |

Outputs go to `bin/<Configuration>/`, with no framework or RID folder
(`AppendTargetFrameworkToOutputPath` and `AppendRuntimeIdentifierToOutputPath` are both
`false`).

## Runtime and link properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmRuntime` | `Freestanding` | `Freestanding` links nothing. `Libc` links a generated `libc.so.6` stub, for a `SharedLibrary` or an `Executable` (`DT_NEEDED libc.so.6`). A `StaticLibrary` or `Objects` project ignores it. |
| `AsmEntryPoint` | empty, which means `_start` | the executable's entry symbol (`-e`). On `Libc`, leave it empty to get the SDK's `crt1` (`_start` → `__libc_start_main` → `main`). Setting it on `Libc` skips `crt1`, and your entry must then do what `crt1` does. |
| `AsmDynamicLinker` | `/lib64/ld-linux-x86-64.so.2` | `PT_INTERP` of a `Libc` executable. |
| `AsmLinkFlags` | empty | appended to every link line (GNU `ld` syntax, which `ld.lld` accepts). Useful values: `-z defs`, `--exclude-libs ALL`, `-rpath $ORIGIN` (with OS-specific quoting; see [Runtimes and linking](guide/runtimes-and-linking.md)), `--strip-debug`. |
| `AsmSharedLibraryPath` | `$(TargetPath)` for a `SharedLibrary` | computed. Overwritten even when you set it. |
| `AsmStaticLibraryPath` | `$(TargetPath)` for a `StaticLibrary`; `$(TargetDir)lib$(AsmLibraryName).a` with `AsmProduceStaticLibrary` | where the archive goes. Set only when empty. |

### Link lines

Every link ends with `-z noexecstack --build-id=sha1 $(AsmLinkFlags)`.

| Output | Command |
|---|---|
| Shared library | `-shared -soname lib<name>.so -z relro -z now --hash-style=gnu`, then the objects, the `AsmLinkLibrary` inputs and, on `Libc`, `--no-as-needed libc.so.6` (the stub) |
| Freestanding executable | `-static -e <AsmEntryPoint or _start>`, then the objects and inputs |
| Libc executable | `-pie --dynamic-linker /lib64/ld-linux-x86-64.so.2 -z relro -z now --hash-style=gnu -e _start crt1.o`, then the objects, the inputs and the stub |
| Static library | the archive is deleted, then `llvm-ar rcsD lib<name>.a <objects>`, so that members of removed sources do not linger |

The build id is derived from the content, and `D` keeps the archive free of timestamps, so
the same inputs give the same outputs. The inputs include each source's absolute path, which
NASM records in the symbol table.

### The libc stub

For `AsmRuntime=Libc`, the SDK assembles `stubs/libc.asm` and links it as a shared object
named `libc.so.6`:

- **What it contains:** about 290 libc functions as empty bodies, plus four pointer-sized
  data objects: `stdin`, `stdout`, `stderr` and `environ`.
- **What linking against it gives:** `DT_NEEDED libc.so.6`, and the PLT and GOT entries.
  At run time, glibc supplies every symbol.
- **Symbol versions:** the stub's symbols are unversioned, so references bind to glibc's
  default versions, the ones a C program compiled today uses.
- **`pthread_*` and `dl*`** are listed as `libc.so.6` symbols, which they are since
  glibc 2.34. That is why Libc programs need glibc 2.34 or later.
- **A function the stub does not list:**
  - an **executable** fails with `undefined symbol: <name>` until you add
    `<AsmLibcSymbol Include="<name>" />`;
  - a **shared library** links anyway, and glibc supplies the function when the library
    loads, unless you link with `-z defs`.

## Assembly properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `EnableDefaultAsmItems` | `true` | adds `**/*.asm` as `AsmSource` and `**/*.inc` as `AsmInclude`, excluding `bin/`, `obj/` and the other `DefaultItemExcludes`. Set `false` to list them yourself. |
| `AsmDefineConstants` | `DEBUG` in Debug, empty otherwise | semicolon-separated `NAME` or `NAME=VALUE`, passed as `-DNAME`. **Setting it replaces `DEBUG`** (see above). Add symbols with `AsmDefine` items instead. |
| `AsmDebugInfo` | `true` in Debug, `false` otherwise | `-g -F dwarf`: DWARF in the objects and in the linked output. |
| `AsmFlags` | empty | extra NASM arguments. They are placed before `-I` and `-D`. Recommended: `-Xvc`, which makes NASM messages MSBuild diagnostics. |
| `TreatWarningsAsErrors` | `false` | adds `-w+error`. NASM 3 warns `implicit DEFAULT ABS is deprecated` on any memory operand in a file without `default rel` or `default abs`. With this property, that warning fails the build. |
| `AsmIntermediateDirectory` | `$(IntermediateOutputPath)asm/` (`obj/<Configuration>/asm/`) | objects in `obj/`, stubs in `stubs/`, generated package props in `pack/`. |

**The warning set is fixed:** `-w+all -w-reloc-rel-dword -w-reloc-abs-qword`.

- The two exceptions fire on relocations that position-independent code needs. The first
  is a RIP-relative reference to another section. The second is a `dq label`, which is
  valid in a writable section.
- Add `-w-<name>` to `AsmFlags` to silence another warning, for example
  `-w-label-orphan`. The name is printed in brackets after each warning.
- The SDK's own stubs are always assembled with `-w+error`.

**The NASM command line, as run** (from the project folder, with forward-slash paths):

```text
nasm -f $(AsmFormat) [-g -F dwarf] -w+all -w-reloc-rel-dword -w-reloc-abs-qword [-w+error] $(AsmFlags)
     -I"<dir>/" ... -D<symbol> ... -o "<obj>/x.o" "<project>/x.asm"
```

## Sharing and pack properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmPackSources` | `false` | also pack `AsmSource` and `AsmInclude` items into the package's `src/`, keeping their relative paths. |
| `AsmPackageLinkKind` | `Static` | what a consuming `.asmproj` links: `Static` takes the package's `.a` when it has one, otherwise its `.so`. `Shared` takes the `.so` when it has one, otherwise its `.a`. |
| `PackageId`, `Version`, `Authors`, `Description`, `PackageLicenseExpression`, `PackageReadmeFile`, … | as for any NuGet package | standard NuGet metadata. `NU5128` (no `lib/`) is suppressed on purpose. |

## Toolchain properties

| Property | Default | Meaning and footguns |
|---|---|---|
| `AsmToolchain` | `Bundled` | `Bundled`: NASM, `lld` (run as `-flavor gnu`) and `llvm-ar` from the toolchain package. `System`: `nasm`, `ld` and `ar` from PATH. Any other value fails with `ASM0002`. |
| `AsmToolchainVersion` | `$(AsmFoundationDefaultToolchainVersion)`, which is `0.1.0` for SDK 0.1.0 | the toolchain package version to download. When it is empty, no download is added. |
| `AsmHostRid` | from `NETCoreSdkRuntimeIdentifier`: `win-<arch>`, `osx-<arch>` or `linux-<arch>` | the build machine. Distribution RIDs (`ubuntu.24.04-x64`) become portable ones. |
| `AsmToolchainPackageId` | `AsmFoundation.Toolchain.$(AsmHostRid)` | packages exist for `win-x64`, `linux-x64` and `osx-arm64` only. |
| `AsmToolchainDirectory` | `<NuGetPackageRoot>/asmfoundation.toolchain.<rid>/<version>/tools/` | set it to use tools from another folder: no download happens, and the folder must hold `nasm`, `lld` and `llvm-ar` (`.exe` on Windows). |
| `AsmNasmCommand`, `AsmLinkerCommand`, `AsmArchiverCommand` | `nasm`, `ld`, `ar` | the commands used with `AsmToolchain=System`. They may include arguments. |

## Properties the SDK sets for you

Read-only in practice. They are set in `Sdk.props`, before the project body.

| Property | Value | Why |
|---|---|---|
| `UsingAsmFoundationSdk` | `true` | detect asm projects in `Directory.Build.*`, targets and package props |
| `AsmFoundationSdkDirectory` | the SDK package root | where `Sdk/`, `targets/` and `stubs/` live |
| `TargetFramework` | `netstandard2.0`, when neither `TargetFramework` nor `TargetFrameworks` is set | NuGet needs a framework to restore; any `.csproj` may reference a netstandard2.0 project |
| `EnableDefaultItems` | `false` | no `Compile`, `None` or `EmbeddedResource` globs |
| `DisableImplicitFrameworkReferences`, `AutomaticallyUseReferenceAssemblyPackages` | `true`, `false` | no NETStandard.Library reference |
| `GenerateAssemblyInfo`, `ProduceReferenceAssembly`, `GenerateDependencyFile`, `GenerateRuntimeConfigurationFiles`, `UseAppHost` | `false` | nothing managed exists |
| `DebugType`, `DebugSymbols` | `none`, `false` | no PDB; the debug information is DWARF in the objects |
| `IncludeBuildOutput`, `CopyBuildOutputToOutputDirectory`, `CopyOutputSymbolsToOutputDirectory` | `false` | the native outputs are written and packed by the SDK's own steps |
| `SkipCopyBuildProduct` | `true` | the same reason |
| `AppendTargetFrameworkToOutputPath`, `AppendRuntimeIdentifierToOutputPath` | `false` | outputs in `bin/<Configuration>/` |
| `NoWarn` | `+NU5128` | a native package has no `lib/` by design |

## Items and their metadata

| Item | Default | Meaning and footguns |
|---|---|---|
| `AsmSource` | `**/*.asm` | one NASM run per item. An `.asm` that another file `%include`s is a source too, which gives `duplicate symbol`. Rename it `.inc`, or `<AsmSource Remove="x.asm" />`. |
| `AsmInclude` | `**/*.inc` | build dependencies: editing one reassembles every source. Also packed under `src/` with `AsmPackSources`. **List include files from folders outside the project here too**, or edits to them will not rebuild. |
| `AsmIncludeDirectory` | `include/` (when it exists), then the project folder | the `-I` paths. Items without `AsmOrigin` come first, in the order added; items with `AsmOrigin` (from packages and references) come after. NASM tries the working directory, which is the project folder, before any of them. |
| `AsmDefine` | none | one `-D` per item: `NAME` or `NAME=VALUE`. Adding these keeps `DEBUG`. |
| `AsmLinkLibrary` | none | extra link inputs (`.a`, `.so`, `.o`), in order, after your objects. References and packages add theirs here. |
| `AsmLibcSymbol` | none | extra function names for the `libc.so.6` stub (`AsmRuntime=Libc`). A `Libc` executable fails with `undefined symbol` until the function is listed. A shared library links anyway, and the function binds at load time. |
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
| `_AsmResolveToolchain` | before `AsmPrepare` | locates the tools, raises `ASM0001`/`ASM0002`, and runs `chmod +x` on the bundled tools on Linux and macOS (NuGet drops the execute bit) |
| `AsmPrepare` | before assembling, linking and packing | computes object paths, the ordered include directories, dependencies, defines and link inputs; depends on `_AsmResolveProjectReferences` |
| `AsmAssemble` | once per out-of-date source (target batching) | Inputs: the source, every dependency `.inc`, the project file. Output: the object |
| `_AsmBuildLibcStub` | `Libc` shared libraries and executables | `stubs/libc.asm` → `obj/…/asm/stubs/libc.so.6` (`-soname libc.so.6`) |
| `_AsmBuildCrt` | `Libc` executables with no `AsmEntryPoint` | `stubs/crt1.asm` → `obj/…/asm/stubs/crt1.o` |
| `AsmLink` | unless `Objects` | Inputs: objects, link inputs, the project file. Outputs: `$(TargetPath)` and the static library |
| `CoreCompile` | replaced | `AsmAssemble;AsmLink` |
| `CreateManifestResourceNames`, `CopyFilesToOutputDirectory` | replaced, empty | no resources, nothing managed to copy |
| `GetTargetPathWithTargetPlatformMoniker` | replaced, returns nothing | so a referencing `.csproj` adds no assembly reference (a `.so` would raise `MSB3246`) |
| `GetAsmLinkInputs` | called by referencing `.asmproj` projects | returns the public include directories, then the static library, else the shared library, else (for `Objects`) the objects |
| `_AsmResolveProjectReferences` | in the referencing project | calls `GetAsmLinkInputs` on each referenced `.asmproj`; references to other project types are left alone |
| `GetCopyToOutputDirectoryItems`, `GetCopyToPublishDirectoryItems` | called by a referencing `.csproj` | return the `.so` (`PreserveNewest`), so it lands next to the program |
| `_AsmWriteConsumerProps` | `dotnet pack` | writes `obj/…/asm/pack/<PackageId>.props` |
| `_AsmPackNativeContent` | `dotnet pack` (`TargetsForTfmSpecificContentInPackage`) | maps include files, sources, libraries and props into the package layout |

## Files the build writes

| Path | What |
|---|---|
| `bin/<Configuration>/lib<name>.so`, `lib<name>.a`, `<name>` | the outputs |
| `obj/<Configuration>/asm/obj/<subfolder>/<file>.o` | one object per source |
| `obj/<Configuration>/asm/stubs/libc.o`, `libc.so.6`, `crt1.o` | the Libc stub and start-up code |
| `obj/<Configuration>/asm/pack/<PackageId>.props` | the consumer props for the package |
| `bin/<Configuration>/<PackageId>.<Version>.nupkg` | `dotnet pack` output, unless `-o` or `PackageOutputPath` says otherwise |

The SDK registers every file in this table except the `.nupkg` in `FileWrites`, so
`dotnet clean` removes them. A file that your own target generates is removed by
`dotnet clean` only if you add it to `FileWrites` too.

## Errors and warnings

| Code or text | Meaning | Fix |
|---|---|---|
| `ASM0001` | The bundled toolchain is not where restore puts it. | Run `dotnet restore`; don't build with `--no-restore` on a clean machine; or use `AsmToolchain=System` or `AsmToolchainDirectory`. |
| `ASM0002` | `AsmToolchain` is neither `Bundled` nor `System`. | Use one of the two. |
| `MSB3073 … exited with code 1` | NASM or the linker failed. | Read the line above it. Add `<AsmFlags>-Xvc</AsmFlags>` so NASM's messages become MSBuild errors with file and line. |
| `NU1101 Unable to find package AsmFoundation.Toolchain.<rid>` | No toolchain package for this build machine or feed. | Use a supported host, add the feed, or use `AsmToolchain=System`. |
| `MSB4236` / SDK not found | `global.json` or `nuget.config` does not lead to `AsmFoundation.Sdk`. | Check `msbuild-sdks` and the feeds (see [The toolchain](toolchain/index.md)). |
| `MSB4025 … ProjectType '' not found` | `.slnx` entry without a type. | Add `Type="13B669BE-BB05-4DDF-9536-439F39A36129"`. |

[Troubleshooting](guide/troubleshooting.md) has the NASM and linker errors.
