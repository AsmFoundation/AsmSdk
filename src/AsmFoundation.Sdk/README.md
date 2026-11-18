# AsmFoundation.Sdk

**Enterprise-grade project, dependency and build management for x86-64
assembly, on MSBuild and NuGet.**

An `.asmproj` sits in a solution next to `.csproj` projects and builds with
`dotnet build`:

- NASM sources become a Linux **shared library** (`.so`), **static library**
  (`.a`) or **executable**.
- Asm libraries are shared as **NuGet packages**, pinned by version like any
  .NET dependency.
- **ProjectReference** and **PackageReference** work as they do in a `.csproj`.
- `dotnet pack` writes a native package that both asm and .NET projects can
  consume.
- **IDEs** (Rider, Visual Studio, VS Code) show an `.asmproj`'s files like a
  `.csproj`'s, and opening a solution never assembles or links.

Nothing needs to be installed. Restore downloads a bundled toolchain (NASM plus
LLVM's `ld.lld` and `llvm-ar`) for Windows, Linux or macOS build machines. Every
build machine produces the same Linux x86-64 ELF output.

## Quick start

`global.json`:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.2.0" } }
```

`Hello/Hello.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>hello</AssemblyName>   <!-- bin/Debug/freestanding/libhello.so -->
  </PropertyGroup>
</Project>
```

`Hello/hello.asm`:

```nasm
default rel
section .text
global hello_add:function
hello_add:                  ; int hello_add(int a, int b)
    lea     eax, [rdi + rsi]
    ret
section .note.GNU-stack noalloc noexec nowrite progbits
```

```sh
dotnet build Hello/Hello.asmproj
```

In a `.slnx`, give the project the generic MSBuild project type:
`<Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />`.

## What a project can produce

| `OutputType` | Output | Notes |
|---|---|---|
| `Library` (default) | `lib<name>.so` | position-independent code required (`default rel`, calls `wrt ..plt`) |
| `StaticLibrary` | `lib<name>.a` | deterministic archive with a symbol index |
| `Exe` | `<name>` | static `_start` (`freestanding`), or a PIE entering `main` (`libc`) |
| `Objects` | `.o` files only | for other projects to link |

`<AsmProduceStaticLibrary>true</AsmProduceStaticLibrary>` also writes
`lib<name>.a` next to a shared library.

## Settings

| Property / item | Default | Meaning |
|---|---|---|
| `TargetFramework` | `freestanding` | `libc`: link glibc dynamically (`DT_NEEDED libc.so.6`) through a generated stub, so no Linux sysroot is needed. An `Exe` then starts in `main`. Needs glibc 2.34+ at run time. `TargetFrameworks=freestanding;libc` builds both, like a multi-targeted `.csproj`. |
| `AssemblyName` | project name | base name of the output |
| `StartupObject` | `_start` | entry symbol of an `Exe` |
| `DefineConstants` | | NASM `-D` symbols. `DEBUG` or `RELEASE`, and `FREESTANDING` or `LIBC`, are added for you, as C# adds `DEBUG` |
| `DebugSymbols` | `true` in Debug | DWARF; `DebugType=none` turns it off |
| `AsmIncludeDirectory` items | `include/`, the project folder | NASM `-I` paths |
| `AsmFlags` | | extra NASM arguments |
| `LinkerArg` items, `ExportsFile` | | extra linker arguments, and the version script (NativeAOT's names) |
| `AsmLinkLibrary` items | | extra `.a` / `.so` link inputs |
| `AsmLibcSymbol` items | | libc functions missing from the stub's list |
| `TreatWarningsAsErrors` | `false` | NASM `-w+error` |
| `AsmToolchain` | `Bundled` | `System` uses `nasm`, `ld` and `ar` from PATH |

## Sharing libraries

- **Between projects:** a `ProjectReference` to another `.asmproj` adds its
  `include/` folder and links its library.
- **To a `.csproj`:** a `ProjectReference` from a `.csproj` builds the
  `.asmproj` and copies its `.so` next to the program, ready for
  `[LibraryImport]`.
- **As a package:** `dotnet pack` writes this layout:

  | Path | Contents |
  |---|---|
  | `include/` | the public include files |
  | `lib/<framework>/_._` | an empty marker per target framework, so NuGet knows which projects may use the package |
  | `native/linux-x64/<tfm>/lib<name>.a` | the static library |
  | `runtimes/linux-x64/native/lib<name>.so` | the shared library (`nativeassets/<framework>/` when the project targets both) |
  | `build/<framework>/<id>.props` | the generated props, per target framework |

  A consuming `.asmproj` gets the package's include files and the library of the nearest
  target framework from `build/<framework>/<id>.props`. A consuming `.csproj` gets the
  `.so`. A `freestanding` project cannot use a `libc`-only package (`NU1202`).

Source, documentation and samples: <https://github.com/AsmFoundation/AsmSdk>.
