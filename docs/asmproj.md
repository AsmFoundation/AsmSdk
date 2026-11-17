# `.asmproj` reference

Everything `AsmFoundation.Sdk` reads from a project. Set properties in a
`<PropertyGroup>` and items in an `<ItemGroup>`, as in any SDK-style project.
Anything not listed here (`Version`, `PackageId`, `Configuration`,
`Directory.Build.props`, …) works as it does for a `.csproj`.

## Output

| Property | Default | Meaning |
|---|---|---|
| `OutputType` | `Library` | `Library` (shared `.so`), `StaticLibrary` (`.a`), `Exe` (executable) or `Objects` (object files only). The SDK reads it and then resets it to `Library` for the managed SDK, which must never see an executable it would try to give an apphost. |
| `AsmOutputKind` | from `OutputType` | `SharedLibrary`, `StaticLibrary`, `Executable` or `Objects`; set it to bypass `OutputType`. |
| `AsmLibraryName` | `AssemblyName` (the project name) | base name: `lib<name>.so`, `lib<name>.a`, or `<name>` for an executable. |
| `AsmProduceStaticLibrary` | `false` | also archive `lib<name>.a` next to a shared library. |
| `AsmTargetRid` | `linux-x64` | where the code runs; only `linux-x64` exists. |
| `AsmFormat` | `elf64` | NASM output format. |

Outputs go to `bin/<Configuration>/` (no framework folder), objects to
`obj/<Configuration>/asm/obj/`.

## Runtime and linking

| Property / item | Default | Meaning |
|---|---|---|
| `AsmRuntime` | `Freestanding` | `Freestanding`: nothing is linked, and the code makes its own system calls. `Libc`: link glibc dynamically through the generated stub (`DT_NEEDED libc.so.6`). |
| `AsmEntryPoint` | `_start` | entry symbol of an executable. On `Libc`, leave it empty to start in `main` through the SDK's `crt1`. |
| `AsmDynamicLinker` | `/lib64/ld-linux-x86-64.so.2` | `PT_INTERP` of a `Libc` executable. |
| `AsmLinkLibrary` items | | extra link inputs (`.a`, `.so`, `.o`), in order. Packages and referenced projects add theirs here. |
| `AsmLibcSymbol` items | | libc functions the stub does not list; the link fails with `undefined symbol` until they are added. |
| `AsmLinkFlags` | | extra linker arguments (GNU `ld` syntax; `ld.lld` accepts it). |

**Link lines:**

- **Shared library:**
  `-shared -soname lib<name>.so -z relro -z now --hash-style=gnu -z noexecstack --build-id=sha1`
- **Freestanding executable:** `-static -e _start -z noexecstack --build-id=sha1`
- **Libc executable:**
  `-pie --dynamic-linker … -z relro -z now -e _start crt1.o … libc.so.6(stub)`
- **Static library:** `llvm-ar rcsD`. The archive is deleted first, so members
  of removed sources do not linger.

The output is deterministic: the build id is derived from the content, and
`D` keeps the archive free of timestamps.

### The libc stub

For `AsmRuntime=Libc`, the SDK assembles `stubs/libc.asm` and links it as a
shared object named `libc.so.6`. Its symbols are empty functions (and four
pointer-sized data objects: `stdin`, `stdout`, `stderr` and `environ`).
Linking against it records `DT_NEEDED libc.so.6` and gives the linker what it
needs to build the PLT and GOT. At run time, glibc supplies every symbol.

Unversioned references bind to glibc's default symbol versions, the ones a C
program compiled today uses.

The stub lists the `pthread_*` and `dl*` functions as part of `libc.so.6`,
which they are since glibc 2.34.

## Assembly

| Property / item | Default | Meaning |
|---|---|---|
| `AsmSource` items | `**/*.asm` | sources; one NASM run per source. |
| `AsmInclude` items | `**/*.inc` | include files of the project (dependencies; packed with `AsmPackSources`). |
| `EnableDefaultAsmItems` | `true` | `false` to list `AsmSource` / `AsmInclude` yourself. |
| `AsmIncludeDirectory` items | `include/` (if present), the project folder | NASM `-I` paths: the project's own first, then those from packages and references. |
| `AsmDefineConstants` | `DEBUG` in Debug | semicolon-separated `NAME` or `NAME=VALUE`, passed as `-D`. |
| `AsmDefine` items | | more `-D` symbols, one per item. |
| `AsmDebugInfo` | `true` in Debug | `-g -F dwarf`. |
| `AsmFlags` | | extra NASM arguments. |
| `TreatWarningsAsErrors` | `false` | adds `-w+error`. |

**Warnings:** `-w+all` except `reloc-rel-dword` and `reloc-abs-qword`. Those
two fire on relocations every position-independent ELF object has (a
RIP-relative reference to another section, a `dq` of a label).

**Incremental builds:** an object is rebuilt when its source, any include file
in its include path, or the project file is newer. A rebuild without changes
starts no tool.

## Sharing

| Property / item | Default | Meaning |
|---|---|---|
| `AsmPublicIncludeDirectory` items | `include/` if present, else the project folder | what a referencing project or a package consumer adds to its include path. |
| `AsmPublicInclude` items | `include/**/*` | files packed into the package's `include/`. |
| `AsmPackSources` | `false` | pack the sources into `src/`, for consumers that assemble them with their own settings. |
| `AsmPackageLinkKind` | `Static` | what a consuming `.asmproj` links: the static library when the package has one (`Static`), or the shared library (`Shared`). |

**A ProjectReference from another `.asmproj`** calls `GetAsmLinkInputs`. It
returns the public include directories, then the static library if one is
produced, else the shared library, else the objects.

**A ProjectReference from a `.csproj`** builds the `.asmproj` and copies its
`.so` (`GetCopyToOutputDirectoryItems` / `GetCopyToPublishDirectoryItems`).
No assembly reference is added: `GetTargetPathWithTargetPlatformMoniker`
returns nothing.

**The generated `build/<id>.props`** (also under `buildTransitive/`) adds
`AsmIncludeDirectory` and `AsmLinkLibrary` items with `AsmOrigin="Package"`,
only in projects with `UsingAsmFoundationSdk=true`. It also defines
`AsmPackageRoot_<id>` (the package folder; non-identifier characters of the id
become `_`), for consumers that read `src/`.

## Toolchain

| Property | Default | Meaning |
|---|---|---|
| `AsmToolchain` | `Bundled` | `System`: use `nasm`, `ld` and `ar` from PATH (GNU binutils), for links that need a real sysroot. |
| `AsmToolchainVersion` | the version this SDK release was tested with | which `AsmFoundation.Toolchain.<rid>` to download. |
| `AsmHostRid` | from the .NET SDK | `win-x64`, `linux-x64`, `osx-arm64`, …; selects the toolchain package. Distribution RIDs (`ubuntu.24.04-x64`) are normalized. |
| `AsmToolchainDirectory` | the restored package's `tools/` | use tools from another folder (no download). |
| `AsmNasmCommand`, `AsmLinkerCommand`, `AsmArchiverCommand` | `nasm`, `ld`, `ar` | commands for `AsmToolchain=System`. |

NuGet does not keep the Unix execute bit, so the SDK restores it on the
bundled tools before each build (Linux and macOS).

## Errors

| Code | Meaning | Fix |
|---|---|---|
| `ASM0001` | The bundled toolchain is not in the package folder. | Run `dotnet restore` (it downloads `AsmFoundation.Toolchain.<rid>`), or set `AsmToolchain=System`. |
| `ASM0002` | `AsmToolchain` is neither `Bundled` nor `System`. | Use one of the two. |

NASM and linker messages are reported as the build's errors, with file and
line.

## Targets

| Target | Does |
|---|---|
| `AsmPrepare` | object paths, include and define arguments, link inputs |
| `AsmAssemble` | one NASM run per out-of-date source |
| `AsmLink` | the shared library, executable and/or static library |
| `GetAsmLinkInputs` | what a referencing `.asmproj` consumes |
| `CoreCompile` | replaced: `AsmAssemble` + `AsmLink` |
