# AsmFoundation.Sdk

**Enterprise-grade project, dependency and build management for assembly, on MSBuild and
NuGet.**

An `.asmproj` sits in a solution next to `.csproj` projects and builds with
`dotnet build`:

- Assembly becomes a **shared library**, **static library** or **program** for **Windows,
  Linux and macOS, on x64 and ARM64**, and for **WebAssembly** (`browser-wasm`, `wasi-wasm`):
  ten runtime identifiers, from any build machine. x64 code is NASM (`.asm`), ARM64 code GNU
  syntax (`.s`), WebAssembly code LLVM's WebAssembly assembly (`.wasm.s`).
- A **C# project that references it** gets the binary of the platform it is built or run for,
  chosen by .NET's own runtime identifier rules, and **C# bindings** the SDK writes from the
  library's `PROTO` and `STRUCT` lines, with nothing declared by hand. C gets a header from
  the same lines.
- Asm libraries are shared as **NuGet packages**, pinned by version like any
  .NET dependency. A package carries its C# bindings and its C header too.
- **ProjectReference** and **PackageReference** work as they do in a `.csproj`.
- `dotnet pack` writes one native package, for every RID, that both asm and .NET projects
  can consume. `dotnet run` starts a program, `dotnet publish` writes it with its libraries,
  and `PackAsTool` packs it as a .NET tool.
- **IDEs** (Rider, Visual Studio, VS Code) show an `.asmproj`'s files like a
  `.csproj`'s, and opening a solution never assembles or links.
- **`dotnet new` templates** (the `AsmFoundation.Templates` package): `asmlib`, `asmexe`,
  `asmstatic`, `asmnet` (a C# app, the asm library it calls and its tests), `asmwasm` and
  `asmwasmapp`.

Nothing needs to be installed. Restore downloads a bundled toolchain (NASM, plus LLVM's
`llvm-mc`, `lld` and `llvm-ar`) for Windows, Linux or macOS build machines, and the SDK writes
the C library stand-ins itself: no sysroot or platform SDK.

## Quick start

`global.json`:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.4.0" } }
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

`Hello/hello.asm`, one source for the three OSes. `ImplicitUsings` (on by default) puts the
SDK's `asmsdk.inc`, `default rel` and Linux's `.note.GNU-stack` in front of it:

```nasm
PROTO int32_t hello_add(int32_t a, int32_t b)
FUNCTION hello_add, 2
    lea     eax, [ARG1 + ARG2]  ; rcx + rdx on Windows, rdi + rsi elsewhere
    RETURN
ENDFUNCTION
```

```sh
dotnet build Hello/Hello.asmproj
```

```text
Hello -> bin/Debug/freestanding/win-x64/hello.dll
Hello -> bin/Debug/freestanding/linux-x64/libhello.so
Hello -> bin/Debug/freestanding/osx-x64/libhello.dylib
```

A C# project with a `ProjectReference` to it calls `Hello.Hello.HelloAdd(40, 2)`: the
namespace is the library's, the class the source file's. C includes `hello.h`, written next to
the libraries. Add `osx-arm64`, `linux-arm64` or `win-arm64` with a `hello.s` (GNU syntax,
through `asmsdk-gnu.inc`). Without `RuntimeIdentifiers`, a project builds for the build
machine's. In a `.slnx`, give the project the generic MSBuild project type:
`<Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />`
(`dotnet sln add` writes it).

## What a project can produce

| `OutputType` | Windows | Linux | macOS | WebAssembly |
|---|---|---|---|---|
| `Library` (default) | `<name>.dll` + `<name>.dll.lib` | `lib<name>.so` | `lib<name>.dylib` | `<name>.wasm`, a module, + `<name>.a`, the archive a .NET WebAssembly app links |
| `StaticLibrary` | `<name>.lib` | `lib<name>.a` | `lib<name>.a` | `<name>.a` |
| `Exe` | `<name>.exe` | `<name>`: static `_start`, or a PIE entering `main` | `<name>`, entering `main` (`libc` only) | `<name>.wasm`, a WASI command (`wasi-wasm` only) |
| `Objects` | object files only, for other projects to link | | | |

`<AsmProduceStaticLibrary>true</AsmProduceStaticLibrary>` also writes the static library
next to a shared one.

## Settings

| Property / item | Default | Meaning |
|---|---|---|
| `RuntimeIdentifiers` | the build machine's RID | the platforms to build: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `osx-x64`, `osx-arm64`, `browser-wasm`, `wasi-wasm`. A referencing project, or `-r`, chooses first. A pack or portable publish that would ship the default alone warns `ASM0013`. |
| `TargetFramework` | `freestanding` | `libc`: link the platform's C library (glibc or musl, the Universal C Runtime, libSystem) through stand-ins the SDK writes, so no sysroot is needed. A program then starts in `main`. `TargetFrameworks=freestanding;libc` builds both, like a multi-targeted `.csproj`. |
| `AssemblyName` | project name | base name of the output |
| `StartupObject` | `_start` | entry symbol of an `Exe` |
| `DefineConstants` | | assembler symbols. `DEBUG` or `RELEASE`, `FREESTANDING` or `LIBC`, `WINDOWS`, `LINUX` or `MACOS` (and `MUSL`), `X64` or `ARM64` are added for you, as C# adds `DEBUG` |
| `DebugSymbols` | `true` in Debug | DWARF, or CodeView and a PDB on Windows; `DebugType=none` turns it off |
| `Compile` items, `None` items | every `.asm` and `.s` file; every other file | the sources, and the rest of the folder. Each `None` item ending in `.inc` is a dependency of every source. `<Compile Remove="..." />` and `EnableDefaultCompileItems` work as in a `.csproj`. |
| `ImplicitUsings` | `enable` | the platform layer in front of every source: `asmsdk.inc`, `default rel`, Linux's `.note.GNU-stack` and `section .text` for NASM, `asmsdk-gnu.inc` for GNU syntax. `disable` gives raw NASM. |
| `AsmIncludeDirectory` items | `include/`, the project folder | `-I` paths |
| `AsmFlags`, `AsmLlvmMcFlags` | | extra NASM and llvm-mc arguments |
| `LinkerArg` items, `ExportsFile` | | extra linker arguments, and the exports (a version script, read on every OS; NativeAOT's names) |
| `NativeLibrary` items | | extra link inputs |
| `AsmLibcSymbol` items | | C library functions missing from the SDK's lists |
| `TreatWarningsAsErrors` | `false` | NASM `-w+error` |
| `NoWarn`, `WarningsAsErrors`, `WarningsNotAsErrors` | | NASM's warning names (`label-orphan`): `-w-<name>`, `-w+error=<name>`, `-w-error=<name>` |
| `AsmGenerateCHeader` | `true` | `<AssemblyName>.h` from the `PROTO` and `STRUCT` lines; a project's own `include/<AssemblyName>.h` wins |
| `AsmToolchain` | `Bundled` | `System` uses `nasm`, `llvm-mc`, a linker and `ar` from PATH |

A C# project that references an asm library sets `AsmGenerateBindings` to `false` to write its
own imports instead of the generated bindings, and shapes them with `AsmBindings` items and
`AsmBindings*` properties, or with `[AsmLibrary]` in its code.

## Sharing libraries

- **Between projects:** a `ProjectReference` to another `.asmproj` adds its
  `include/` folder and links its library, built for the same RID.
- **To a `.csproj`:** a `ProjectReference` from a `.csproj` builds the `.asmproj` for the
  RIDs the build needs. A portable build gets each library under `runtimes/<rid>/native/`,
  listed in `deps.json`, so the .NET host loads the right one; a RID-specific build gets the
  best match, by the RID graph, beside the program. Ready for `[LibraryImport]`, also through
  a C# wrapper library in between, and for every publish mode: a single file (the library
  beside it, or inside it with `IncludeNativeLibrariesForSelfExtract`), NativeAOT, trimming,
  container images.
- **Somewhere else:** `BinariesCopyPath` on the reference (or as the project's property, or on
  a `PackageReference` to an asm package) puts the libraries in `native/{rid}`, `RidFolders`,
  `AppFolder` or any folder of the output. `deps.json` names the place, and a generated
  loader covers NativeAOT. `PrivateAssets="all"` on the reference packs the libraries into
  the .NET project's own package.
- **As a package:** `dotnet pack` writes this layout:

  | Path | Contents |
  |---|---|
  | `include/` | the public include files, and `<AssemblyName>.h`, the C header |
  | `lib/<framework>/_._` | an empty marker per target framework, so NuGet knows which projects may use the package |
  | `native/<rid>/<tfm>/` | the static library, a Windows DLL's import library, and a WebAssembly library's module and archive |
  | `runtimes/<rid>/native/` | the shared library (`nativeassets/<framework>/` when the project targets both) |
  | `build/<framework>/<id>.props` | the generated props, per target framework |
  | `build/<framework>/<id>.targets`, `buildTransitive/<framework>/<id>.targets` | for the package's .NET users: `BinariesCopyPath` with a shared library, and the bindings' targets with `PROTO` or `STRUCT` lines |
  | `contentFiles/cs/net7.0/AsmBindings/`, `build/bindings/` | the C# bindings: the baseline NuGet compiles into each C# consumer, the API file and the bindings generator |

  A consuming `.asmproj` gets the package's include files and the library of its RID and
  nearest target framework from `build/<framework>/<id>.props`. A consuming `.csproj` gets the
  `runtimes/` libraries and the bindings, and a .NET WebAssembly app links the archive. A
  `freestanding` project cannot use a `libc`-only package (`NU1202`).

Source, documentation and samples: <https://github.com/AsmFoundation/AsmSdk>.
