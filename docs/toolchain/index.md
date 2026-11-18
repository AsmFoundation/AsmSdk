# The toolchain

AsmSdk builds with three tools: an assembler (NASM), an ELF linker (`ld.lld`) and an
archiver (`llvm-ar`). A fourth, `llvm-readobj`, ships alongside them for inspecting what was
built. All of them come from one NuGet package per build machine, so the .NET SDK is the
only thing a build machine needs.

| Package | Build machine |
|---|---|
| [`AsmFoundation.Toolchain.win-x64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.win-x64) | Windows x64 |
| [`AsmFoundation.Toolchain.linux-x64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.linux-x64) | Linux x64 (glibc) |
| [`AsmFoundation.Toolchain.osx-arm64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.osx-arm64) | macOS on Apple silicon |

Every package produces the same output: Linux x86-64 ELF objects, libraries and executables.
A team can mix Windows, Linux and macOS machines.

## How a build gets it

- **You never reference the package yourself.** Restore adds this to every `.asmproj`:

  ```xml
  <PackageDownload Include="AsmFoundation.Toolchain.$(AsmHostRid)" Version="[$(AsmToolchainVersion)]" />
  ```

  NuGet extracts the package into the global packages folder: `~/.nuget/packages`, or
  `$NUGET_PACKAGES` when set.
- **`AsmToolchainVersion` defaults to the version the SDK release was tested with:** `0.1.0`
  for SDK 0.1.0 and for SDK 0.2.0, which kept the same tools. Toolchain packages carry their
  own version, so a new SDK release does not download unchanged tools again.
- **A `PackageDownload` is not a dependency.** Packages built from your projects do not list
  the toolchain, and their consumers download their own.
- **Before each build,** the SDK checks that the tools are present. If one is missing, the
  build stops with `ASM0001` and the fix: run `dotnet restore`.
- **On Linux and macOS** the SDK also restores the tools' execute bit, which NuGet does not
  keep.

## What is in it

Toolchain 0.1.0:

| Tool | Version | Used for |
|---|---|---|
| `nasm` | 3.02 | assembling every source (`nasm -f elf64`) |
| `lld` | LLVM 23.1.5 | linking shared libraries and executables, run as `lld -flavor gnu` (`ld.lld`) |
| `llvm-ar` | LLVM 23.1.5 | writing static libraries (`llvm-ar rcsD`) |
| `llvm-readobj` | LLVM 23.1.5 | not used by the build; inspecting outputs |

[The tools](tools.md) shows the exact command lines the build runs, and how to run each
tool yourself.

## Supported build machines

`AsmHostRid` comes from the .NET SDK's own runtime identifier:

- the operating system becomes `win-`, `osx-` or `linux-`, and the architecture is kept;
- distribution identifiers such as `ubuntu.24.04-x64` become `linux-x64`.

| Build machine | `AsmHostRid` | Package for toolchain 0.1.0 |
|---|---|---|
| Windows x64 | `win-x64` | yes |
| Linux x64 (glibc) | `linux-x64` | yes |
| macOS on Apple silicon | `osx-arm64` | yes |
| Linux x64 with musl (Alpine) | `linux-x64` | the same package, but its LLVM tools are glibc programs |
| macOS on Intel | `osx-x64` | no |
| Linux arm64 | `linux-arm64` | no |
| Windows arm64 | `win-arm64` | no |

On a machine without a package, restore fails with
`NU1101: Unable to find package AsmFoundation.Toolchain.<rid>`. You have three options:

- **Use the machine's own tools** with `AsmToolchain=System`.
- **Point `AsmToolchainDirectory`** at a folder of tools built for that machine.
- **Emulate a supported machine:** set `AsmHostRid` to one the machine can run, such as
  `win-x64` on Windows arm64.

All three are described in [Pinning, offline and system tools](system-and-offline.md).
