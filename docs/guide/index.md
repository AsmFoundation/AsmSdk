# What is AsmSdk

AsmSdk is the AsmFoundation's project, dependency and build management for assembly, on
MSBuild and NuGet. Its NuGet package is `AsmFoundation.Sdk`.

An `.asmproj` is to assembly what a `.csproj` is to C#:

- **`dotnet build`** assembles and links it, for every platform it lists.
- **`ProjectReference` and `PackageReference`** share code between projects, with the same
  version resolution and transitive restore as any .NET project.
- **`dotnet pack`** turns it into a versioned NuGet package that both assembly and .NET
  projects can consume.
- **It sits in the same solution** (`.slnx` or `.sln`) as the C# projects that call it. A C#
  project that references it gets the library of the platform it is built or run for,
  chosen by .NET's own runtime identifier rules.

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>hello</AssemblyName>
    <RuntimeIdentifiers>win-x64;linux-x64;linux-arm64;osx-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

## What it builds

Seven platforms, named by .NET's runtime identifiers (RIDs): `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `linux-musl-x64` (Alpine), `osx-x64` and `osx-arm64`. x64 code
is NASM (`.asm`). ARM64 code is GNU syntax (`.s`), assembled by LLVM's `llvm-mc`. Any build
machine builds every platform. See [Platforms and runtime identifiers](platforms.md).

| `OutputType` | Windows | Linux | macOS |
|---|---|---|---|
| `Library` (default) | `<name>.dll`, with its import library `<name>.dll.lib` | `lib<name>.so` | `lib<name>.dylib` |
| `StaticLibrary` | `<name>.lib` | `lib<name>.a` | `lib<name>.a` |
| `Exe` | `<name>.exe` | `<name>`: static, entering `_start`, or a PIE entering `main` | `<name>`, entering `main` (C library only) |
| `Objects` | `.obj` files for another project to link | `.o` files | `.o` files |

Outputs go to `bin/<Configuration>/<TargetFramework>/<rid>/`.

## Nothing to install

- **The toolchain is a NuGet package.** Restore downloads `AsmFoundation.Toolchain` for
  the machine you build on: NASM 3.02, plus LLVM 23.1.5's `lld` (all three linkers),
  `llvm-mc`, `llvm-ar` and `llvm-readobj`. See [The toolchain](../toolchain/index.md).
- **The download is pinned.** Each SDK release names the toolchain version it was tested
  with, and every file behind the toolchain packages is pinned by SHA-256.
- **The C library needs no sysroot.** A project that uses glibc, musl, the Universal C Runtime
  or libSystem links against stand-ins the SDK writes itself. No headers, libraries or
  platform SDK are needed, on any build machine.

## How it works

AsmSdk rides on `Microsoft.NET.Sdk`, the way Microsoft's NoTargets SDK does:

- **The standard .NET parts:** restore, references, versioning, pack and solution builds.
- **What AsmSdk replaces:**
  - compilation, which becomes assemble and link, once per RID;
  - the steps that only make sense for managed code: no assembly, no apphost, and no
    assembly reference added to a `.csproj` that references an `.asmproj`.

[Extending the build](extending.md) shows the target graph and its extension points.

## Where to go next

- **New to it:** [Getting started](getting-started.md) builds a shared library, a program
  and a C# caller for seven platforms in a few minutes.
- **Several platforms:** [Platforms and runtime identifiers](platforms.md) says which file
  builds which platform, and how one source serves Windows, Linux and macOS.
- **Writing the code:** [Writing NASM for AsmSdk](writing-nasm.md) lists the rules that
  decide whether code links, on each OS, and the error each rule prevents.
- **Every setting:** the [`.asmproj` reference](../asmproj.md).
- **Something failed:** [Troubleshooting](troubleshooting.md) is organized by error
  message.
