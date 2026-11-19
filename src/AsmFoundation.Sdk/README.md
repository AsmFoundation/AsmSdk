# AsmFoundation.Sdk

**Enterprise-grade project, dependency and build management for assembly, on MSBuild and
NuGet.**

An `.asmproj` sits in a solution next to `.csproj` projects and builds with
`dotnet build`:

- Assembly becomes a **shared library**, **static library** or **program** for **Windows,
  Linux and macOS, on x64 and ARM64**: seven runtime identifiers, from any build machine.
  x64 code is NASM (`.asm`), ARM64 code GNU syntax (`.s`).
- A **C# project that references it** gets the binary of the platform it is built or run for,
  chosen by .NET's own runtime identifier rules, with no setup of its own.
- Asm libraries are shared as **NuGet packages**, pinned by version like any
  .NET dependency.
- **ProjectReference** and **PackageReference** work as they do in a `.csproj`.
- `dotnet pack` writes one native package, for every RID, that both asm and .NET projects
  can consume.
- **IDEs** (Rider, Visual Studio, VS Code) show an `.asmproj`'s files like a
  `.csproj`'s, and opening a solution never assembles or links.

Nothing needs to be installed. Restore downloads a bundled toolchain (NASM, plus LLVM's
`llvm-mc`, `lld` and `llvm-ar`) for Windows, Linux or macOS build machines, and the SDK writes
the C library stand-ins itself: no sysroot or platform SDK.

## Quick start

`global.json`:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.3.0" } }
```

`Hello/Hello.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>hello</AssemblyName>
    <RuntimeIdentifiers>win-x64;linux-x64;osx-x64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

`Hello/hello.asm`, one source for the three OSes through the SDK's `asmsdk.inc`:

```nasm
default rel
%include "asmsdk.inc"

section .text
FUNCTION hello_add, 2           ; int hello_add(int a, int b)
    lea     eax, [ARG1 + ARG2]  ; rcx + rdx on Windows, rdi + rsi elsewhere
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```

```sh
dotnet build Hello/Hello.asmproj
```

```text
Hello -> bin/Debug/freestanding/win-x64/hello.dll
Hello -> bin/Debug/freestanding/linux-x64/libhello.so
Hello -> bin/Debug/freestanding/osx-x64/libhello.dylib
```

Add `osx-arm64`, `linux-arm64` or `win-arm64` with a `hello.s` (GNU syntax, through
`asmsdk-gnu.inc`). In a `.slnx`, give the project the generic MSBuild project type:
`<Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />`.

## What a project can produce

| `OutputType` | Windows | Linux | macOS |
|---|---|---|---|
| `Library` (default) | `<name>.dll` + `<name>.dll.lib` | `lib<name>.so` | `lib<name>.dylib` |
| `StaticLibrary` | `<name>.lib` | `lib<name>.a` | `lib<name>.a` |
| `Exe` | `<name>.exe` | `<name>`: static `_start`, or a PIE entering `main` | `<name>`, entering `main` (`libc` only) |
| `Objects` | object files only, for other projects to link | | |

`<AsmProduceStaticLibrary>true</AsmProduceStaticLibrary>` also writes the static library
next to a shared one.

## Settings

| Property / item | Default | Meaning |
|---|---|---|
| `RuntimeIdentifiers` | none: required | the platforms to build: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64`. Or let a referencing project, or `-r`, choose. |
| `TargetFramework` | `freestanding` | `libc`: link the platform's C library (glibc or musl, the Universal C Runtime, libSystem) through stand-ins the SDK writes, so no sysroot is needed. A program then starts in `main`. `TargetFrameworks=freestanding;libc` builds both, like a multi-targeted `.csproj`. |
| `AssemblyName` | project name | base name of the output |
| `StartupObject` | `_start` | entry symbol of an `Exe` |
| `DefineConstants` | | assembler symbols. `DEBUG` or `RELEASE`, `FREESTANDING` or `LIBC`, `WINDOWS`, `LINUX` or `MACOS` (and `MUSL`), `X64` or `ARM64` are added for you, as C# adds `DEBUG` |
| `DebugSymbols` | `true` in Debug | DWARF, or CodeView and a PDB on Windows; `DebugType=none` turns it off |
| `AsmIncludeDirectory` items | `include/`, the project folder | `-I` paths |
| `AsmFlags`, `AsmLlvmMcFlags` | | extra NASM and llvm-mc arguments |
| `LinkerArg` items, `ExportsFile` | | extra linker arguments, and the exports (a version script, read on every OS; NativeAOT's names) |
| `AsmLinkLibrary` items | | extra link inputs |
| `AsmLibcSymbol` items | | C library functions missing from the SDK's lists |
| `TreatWarningsAsErrors` | `false` | NASM `-w+error` |
| `AsmToolchain` | `Bundled` | `System` uses `nasm`, `llvm-mc`, a linker and `ar` from PATH |

## Sharing libraries

- **Between projects:** a `ProjectReference` to another `.asmproj` adds its
  `include/` folder and links its library, built for the same RID.
- **To a `.csproj`:** a `ProjectReference` from a `.csproj` builds the `.asmproj` for the
  RIDs the build needs. A portable build gets each library under `runtimes/<rid>/native/`,
  listed in `deps.json`, so the .NET host loads the right one; a RID-specific build gets the
  best match, by the RID graph, beside the program. Ready for `[LibraryImport]`.
- **As a package:** `dotnet pack` writes this layout:

  | Path | Contents |
  |---|---|
  | `include/` | the public include files |
  | `lib/<framework>/_._` | an empty marker per target framework, so NuGet knows which projects may use the package |
  | `native/<rid>/<tfm>/` | the static library, and a Windows DLL's import library |
  | `runtimes/<rid>/native/` | the shared library (`nativeassets/<framework>/` when the project targets both) |
  | `build/<framework>/<id>.props` | the generated props, per target framework |

  A consuming `.asmproj` gets the package's include files and the library of its RID and
  nearest target framework from `build/<framework>/<id>.props`. A consuming `.csproj` gets the
  `runtimes/` libraries. A `freestanding` project cannot use a `libc`-only package
  (`NU1202`).

Source, documentation and samples: <https://github.com/AsmFoundation/AsmSdk>.
