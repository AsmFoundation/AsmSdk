# AsmSdk

[![CI](https://github.com/AsmFoundation/AsmSdk/actions/workflows/ci.yml/badge.svg)](https://github.com/AsmFoundation/AsmSdk/actions/workflows/ci.yml)
[![NuGet: AsmFoundation.Sdk](https://img.shields.io/nuget/v/AsmFoundation.Sdk.svg?label=AsmFoundation.Sdk)](https://www.nuget.org/packages/AsmFoundation.Sdk)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-asmfoundation.github.io%2FAsmSdk-06706d.svg)](https://asmfoundation.github.io/AsmSdk/)

**`.asmproj`: x86-64 assembly as a first-class .NET-style project.** This repo
holds the MSBuild SDK behind it.

- It builds NASM sources into Linux shared libraries, static libraries and
  executables with `dotnet build`.
- It shares asm libraries as versioned NuGet packages.
- An `.asmproj` sits in the same solution as `.csproj` projects that
  P/Invoke it.
- Nothing needs to be installed: restore downloads a bundled toolchain (NASM 3.02
  plus LLVM 23.1.5's `ld.lld` and `llvm-ar`) for Windows, Linux or macOS. Every
  build machine produces the same Linux x86-64 ELF.

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AsmLibraryName>hello</AsmLibraryName>   <!-- bin/Debug/libhello.so -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Hello.Static" Version="1.0.0" />   <!-- an asm package -->
  </ItemGroup>
</Project>
```

Part of the [AsmFoundation](https://github.com/AsmFoundation). AsmSdk is the build
system for [GrpcAsm](https://github.com/AsmFoundation/GrpcAsm), a gRPC server and
client in assembly (in development), and
[KvAsm](https://github.com/AsmFoundation/KvAsm) is planned to move onto it.

## Why

Assembly projects usually live outside the .NET build: a Makefile, a Docker
toolchain image, and libraries copied around by hand. AsmSdk brings the parts
the .NET world takes for granted to them:

- **One build command** for the whole solution, asm and C# together.
- **Real dependency management:** an asm library is a NuGet package with a
  version, so other asm projects consume it with `PackageReference`. Transitive
  dependencies flow, and restore fetches them.
- **No install step:** the toolchain is a NuGet package too, pinned by version
  and verified by SHA-256 when it is built.
- **Incremental builds** that are exact: a rebuild without changes starts no
  tool, and editing an include file reassembles only the sources that can
  include it.

## Quick start

1. `global.json` next to your solution:

   ```json
   { "msbuild-sdks": { "AsmFoundation.Sdk": "0.1.0" } }
   ```

2. `Hello/Hello.asmproj` (as above), and any number of `.asm` files next to it.
   Every `*.asm` is a source and every `*.inc` an include file, the way every
   `*.cs` is compiled in a `.csproj`.
3. `dotnet build`.

In a `.slnx` solution, give the project the generic MSBuild project type:

```xml
<Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />
```

## What it builds

| `OutputType` | Output | Notes |
|---|---|---|
| `Library` (default) | `lib<name>.so` | position-independent code (`default rel`, calls `wrt ..plt`) |
| `StaticLibrary` | `lib<name>.a` | deterministic archive with a symbol index |
| `Exe` | `<name>` | `_start` in a static executable, or `main` in a PIE on glibc |
| `Objects` | `.o` files | for another project to link |

The code runs on its own (`AsmRuntime=Freestanding`, the default, with raw
system calls) or on glibc (`AsmRuntime=Libc`).

- **How Libc links without a sysroot:** the SDK links against a generated
  `libc.so.6` stub. The output gets `DT_NEEDED libc.so.6` and PLT entries, and
  the real glibc (2.34 or later) is used at run time.
- **An `Exe` on glibc** starts in `main`, through a `_start` that calls
  `__libc_start_main` as glibc's `crt1.o` does.

Every property, item and error is described in [docs/asmproj.md](docs/asmproj.md), and
the guides are on the [documentation site](https://asmfoundation.github.io/AsmSdk/).

## Sharing code

| From → to | Mechanism | Effect |
|---|---|---|
| `.asmproj` → `.asmproj` | `ProjectReference` | adds the other project's `include/` folder and links its library |
| `.csproj` → `.asmproj` | `ProjectReference` | builds it and copies its `.so` next to the program for `[LibraryImport]`; no assembly reference |
| package → `.asmproj` | `PackageReference` | the package's generated `build/<id>.props` adds its `include/` and its library |
| package → `.csproj` | `PackageReference` | NuGet copies `runtimes/linux-x64/native/*.so` |

`dotnet pack` on an `.asmproj` writes that package:

| Path | Contents |
|---|---|
| `include/` | public include files |
| `src/` | the sources, with `AsmPackSources=true` |
| `native/linux-x64/lib<name>.a` | the static library |
| `runtimes/linux-x64/native/lib<name>.so` | the shared library |
| `build/` and `buildTransitive/` | the generated props |

## Samples

[`samples/`](samples) covers every case, and [`tests/run.py`](tests/run.py)
builds and checks it on every CI run:

| Sample | Shows |
|---|---|
| `Hello.Library` | freestanding shared library |
| `Hello.Static` | static library with public includes, packed as a NuGet package |
| `Hello.App` | freestanding executable linking `Hello.Static` through a ProjectReference |
| `Hello.Libc` | `main()` program calling `printf` on glibc |
| `Hello.LibcLibrary` | shared library calling `strlen` through its PLT |
| `consumer/Hello.Consumer` | consumes the `Hello.Static` package by version |
| `Hello.Net` | C# program P/Invoking both shared libraries |

## How it works

- **The base:** `AsmFoundation.Sdk` rides on `Microsoft.NET.Sdk`, as Microsoft's
  NoTargets SDK does. Restore, references, versioning, pack and solution builds
  are the standard ones.
- **What it replaces:** compilation (`CoreCompile` becomes assemble + link), and
  the few managed steps that make no sense for native code (no assembly, no
  apphost, no reference added to a referencing `.csproj`).
- **Restore:** a `PackageDownload` fetches `AsmFoundation.Toolchain.<host-rid>`.
- **The toolchain packages** are built by
  [`toolchain/fetch.py`](toolchain/fetch.py) from pins in
  [`toolchain/versions.json`](toolchain/versions.json):
  - the official NASM binary on Windows;
  - NASM built from the official source on Linux (statically) and on macOS
    (arm64);
  - the official LLVM release's `lld`, `llvm-ar` and `llvm-readobj`.

## Limits

- **The output is Linux x86-64 ELF.** NASM syntax, System V ABI.
- **Libc programs need glibc 2.34+ at run time.** That is when the `pthread_*`
  and `dl*` functions moved into `libc.so.6`.
- **Linking against a static glibc** needs a real sysroot. Use
  `AsmToolchain=System` on Linux for that.
- **Tests run on Linux.** On Windows, use WSL or Docker: the test runner does
  both automatically when available.

## Development

```sh
python toolchain/fetch.py --rid win-x64     # or linux-x64 / osx-arm64: stages the tools
python tests/run.py                         # packs, builds the samples, checks and runs them
```

CI builds the toolchain on each OS, then runs `tests/run.py` on Windows, Linux
and macOS. Releases are tags: `vX.Y.Z` publishes to nuget.org through trusted
publishing ([release.yml](.github/workflows/release.yml)).

## License

[Apache-2.0](LICENSE). The toolchain packages also contain NASM (BSD-2-Clause)
and LLVM tools (Apache-2.0 WITH LLVM-exception); see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
