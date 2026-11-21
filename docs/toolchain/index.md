# The toolchain

AsmSdk builds with four tools: two assemblers, NASM for x64 code and LLVM's `llvm-mc` for
GNU-syntax code (ARM64), a linker (`lld`, which links ELF, PE/COFF and Mach-O) and an archiver
(`llvm-ar`). A fifth, `llvm-readobj`, ships alongside them for inspecting what was built. All
of them come from one NuGet package per build machine, so the .NET SDK is the only thing a
build machine needs.

| Package | Build machine |
|---|---|
| [`AsmFoundation.Toolchain.win-x64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.win-x64) | Windows x64 |
| [`AsmFoundation.Toolchain.win-arm64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.win-arm64) | Windows on Arm |
| [`AsmFoundation.Toolchain.linux-x64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.linux-x64) | Linux x64 (glibc) |
| [`AsmFoundation.Toolchain.linux-arm64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.linux-arm64) | Linux ARM64 (glibc) |
| [`AsmFoundation.Toolchain.osx-arm64`](https://www.nuget.org/packages/AsmFoundation.Toolchain.osx-arm64) | macOS on Apple silicon |

Every package builds the same outputs, for all seven runtime identifiers: Windows, Linux
(glibc and musl) and macOS, on x64 and ARM64. A team can mix Windows, Linux and macOS
machines.

## How a build gets it

- **You never reference the package yourself.** Restore adds this to every `.asmproj`:

  ```xml
  <PackageDownload Include="AsmFoundation.Toolchain.$(AsmHostRid)" Version="[$(AsmToolchainVersion)]" />
  ```

  NuGet extracts the package into the global packages folder: `~/.nuget/packages`, or
  `$NUGET_PACKAGES` when set.
- **`AsmToolchainVersion` defaults to the version the SDK release was tested with:** `0.1.0`
  for SDK 0.1.0 and 0.2.0, and `0.2.0` for SDK 0.3.0, which needs its `llvm-mc`. Toolchain
  packages carry their own version, so a new SDK release does not download unchanged tools
  again.
- **A `PackageDownload` is not a dependency.** Packages built from your projects do not list
  the toolchain, and their consumers download their own.
- **Before each build,** the SDK checks that the tools are present. If one is missing, the
  build stops with `ASM0001` and the fix: run `dotnet restore`.
- **On Linux and macOS** the SDK also restores the tools' execute bit, which NuGet does not
  keep.

## What is in it

Toolchain 0.2.0:

| Tool | Version | Used for |
|---|---|---|
| `nasm` | 3.02 | assembling `.asm` sources: `-f win64`, `elf64` or `macho64` |
| `llvm-mc` | LLVM 23.1.5 | assembling `.s` sources (GNU syntax), for the target triple of each RID. New in 0.2.0 |
| `lld` | LLVM 23.1.5 | linking: `lld -flavor gnu` (`ld.lld`) for Linux, `-flavor link` (`lld-link`) for Windows, `-flavor darwin` (`ld64.lld`) for macOS; and writing import libraries (`-flavor link -lib`) |
| `llvm-ar` | LLVM 23.1.5 | writing static libraries (`llvm-ar rcsD`), in each OS's archive format |
| `llvm-readobj` | LLVM 23.1.5 | not used by the build; inspecting outputs |

Toolchain 0.1.0 had no `llvm-mc`: a `.s` source, or an ARM64 `libc` build, stops with
`ASM0004` there.

[The tools](tools.md) shows the exact command lines the build runs, and how to run each
tool yourself.

## Supported build machines

`AsmHostRid` comes from the .NET SDK's own runtime identifier:

- the operating system becomes `win-`, `osx-` or `linux-`, and the architecture is kept;
- distribution identifiers such as `ubuntu.24.04-x64` become `linux-x64`.

| Build machine | `AsmHostRid` | Toolchain 0.2.0 | Toolchain 0.1.0 |
|---|---|---|---|
| Windows x64 | `win-x64` | yes | yes |
| Windows on Arm | `win-arm64` | yes: native LLVM tools; NASM's x64 build runs under Windows' x64 emulation | no |
| Linux x64 (glibc) | `linux-x64` | yes | yes |
| Linux ARM64 (glibc) | `linux-arm64` | yes: NASM built from source | no |
| macOS on Apple silicon | `osx-arm64` | yes | yes |
| Linux x64 with musl (Alpine) | `linux-musl-x64` | no: LLVM's Linux tools are glibc programs | no |
| macOS on Intel | `osx-x64` | no: LLVM publishes no Intel Mac build | no |

On a machine without a package, the build stops with `ASM0006`, before restore tries to
download one. You have three options:

- **Use the machine's own tools** with `AsmToolchain=System`: Homebrew's or Alpine's `nasm`,
  `llvm` and `lld` packages have all of them.
- **Point `AsmToolchainDirectory`** at a folder of tools built for that machine.
- **Emulate a supported machine:** set `AsmHostRid` to one the machine can run.

All three are described in [Pinning, offline and system tools](system-and-offline.md).
