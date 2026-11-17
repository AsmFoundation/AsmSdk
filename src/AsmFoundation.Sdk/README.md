# AsmFoundation.Sdk

An MSBuild SDK for x86-64 assembly. An `.asmproj` sits in a solution next to
`.csproj` projects and builds with `dotnet build`:

- NASM sources become a Linux **shared library** (`.so`), **static library**
  (`.a`) or **executable**.
- Asm libraries are shared as **NuGet packages**, pinned by version like any
  .NET dependency.
- **ProjectReference** and **PackageReference** work as they do in a `.csproj`.
- `dotnet pack` writes a native package that both asm and .NET projects can
  consume.

Nothing needs to be installed. Restore downloads a bundled toolchain (NASM plus
LLVM's `ld.lld` and `llvm-ar`) for Windows, Linux or macOS build machines. Every
build machine produces the same Linux x86-64 ELF output.

## Quick start

`global.json`:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.1.0" } }
```

`Hello/Hello.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AsmLibraryName>hello</AsmLibraryName>   <!-- bin/Debug/libhello.so -->
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
| `Exe` | `<name>` | static `_start` (freestanding), or a PIE entering `main` (see `AsmRuntime`) |
| `Objects` | `.o` files only | for other projects to link |

`<AsmProduceStaticLibrary>true</AsmProduceStaticLibrary>` also writes
`lib<name>.a` next to a shared library.

## Settings

| Property / item | Default | Meaning |
|---|---|---|
| `AsmLibraryName` | project name | base name of the output |
| `AsmRuntime` | `Freestanding` | `Libc`: link glibc dynamically (`DT_NEEDED libc.so.6`) through a generated stub, so no Linux sysroot is needed. An `Exe` then starts in `main`. Needs glibc 2.34+ at run time. |
| `AsmEntryPoint` | `_start` | entry symbol of an `Exe` |
| `AsmDefineConstants`, `AsmDefine` items | `DEBUG` in Debug | NASM `-D` symbols |
| `AsmIncludeDirectory` items | `include/`, the project folder | NASM `-I` paths |
| `AsmFlags`, `AsmLinkFlags` | | extra NASM / linker arguments |
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
  | `native/linux-x64/lib<name>.a` | the static library |
  | `runtimes/linux-x64/native/lib<name>.so` | the shared library |
  | `build/<id>.props` | the generated props |

  A consuming `.asmproj` gets the package's include files and library from
  `build/<id>.props`. A consuming `.csproj` gets the `.so`.

Source, documentation and samples: <https://github.com/AsmFoundation/AsmSdk>.
