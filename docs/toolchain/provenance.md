# How the packages are built

Every toolchain package is built from official upstream releases, pinned by SHA-256. The
build is in the AsmSdk repository:
[`toolchain/fetch.cs`](https://github.com/AsmFoundation/AsmSdk/blob/main/toolchain/fetch.cs),
a .NET 10 file-based app, reads its pins from
[`toolchain/versions.json`](https://github.com/AsmFoundation/AsmSdk/blob/main/toolchain/versions.json).

## Where each tool comes from

| Tool | Windows x64 and Windows on Arm | Linux x64 and Linux ARM64 | macOS arm64 |
|---|---|---|---|
| `nasm` 3.02 | the official NASM Windows binary (x64; Windows on Arm runs it under its x64 emulation, since NASM publishes no ARM64 Windows build) | built from the official NASM source on a machine of that CPU, statically linked and stripped | built from the official NASM source, natively for arm64 |
| `llvm-mc`, `lld`, `llvm-ar`, `llvm-readobj` (LLVM 23.1.5) | the official LLVM release archive for that CPU | the official LLVM release archive for that CPU (binaries stripped) | the official LLVM release archive |

- **Only four members are extracted from LLVM.** The release archives are large, so
  `fetch.cs` streams them and keeps only `llvm-mc`, `lld`, `llvm-ar` and `llvm-readobj`. It decodes
  `.tar.xz` with SharpCompress, since .NET has no xz decoder. On the 901 MB Windows archive
  that took 83.5 s, against 78.0 s for Python's lzma, the decoder of the earlier `fetch.py`,
  and staged byte-identical tools.
- **macOS binaries are not stripped,** because stripping would break their code
  signatures.

## The pins

Toolchain package version 0.2.0 (0.1.0 used the same downloads, minus the two ARM64 ones):

| Download | SHA-256 |
|---|---|
| `nasm-3.02.tar.xz` (source) | `87336eba53b4acfe917424ab5d500d2b0054d9f5148d35c2273ccf2cfb712f0d` |
| `nasm-3.02-win64.zip` | `161d0bfaff53c2f9e9f3e69fd0672323ebabafd1268976a5cec11be92a19aee7` |
| `clang+llvm-23.1.5-x86_64-pc-windows-msvc.tar.xz` | `62fdada7f6fa2b73856486dfbbdba3d2c1882991621615bcb55c60207611c6d4` |
| `clang+llvm-23.1.5-aarch64-pc-windows-msvc.tar.xz` | `f06382f2c99c68a4e80f7044446df1fcf52bc3cc05158626bd0b966d51a5369a` |
| `LLVM-23.1.5-Linux-X64.tar.xz` | `f72edb0b36580ece7f52e23858f00c18fad4e87395ea7ad05d7e55c39f8fc78a` |
| `LLVM-23.1.5-Linux-ARM64.tar.xz` | `44f1e4b7a125b2763d56386a199928dd57ec32e151cc6042dce2f7b0da7bb651` |
| `LLVM-23.1.5-macOS-ARM64.tar.xz` | `aba592b4a15e8f90c634c5367b51ec8418b8bcd2829865db331a17dc9418dcff` |
| LLVM `LICENSE.TXT` (llvmorg-23.1.5) | `8d85c1057d742e597985c7d4e6320b015a9139385cff4cbae06ffc0ebe89afee` |

`fetch.cs` verifies each hash before it opens the file. Inside each package,
`tools/VERSIONS.txt` records the exact tool versions.

## Building a toolchain package yourself

```sh
dotnet run toolchain/fetch.cs -- --rid win-x64   # or win-arm64, linux-x64, linux-arm64, osx-arm64: downloads, verifies, stages
dotnet pack toolchain/AsmFoundation.Toolchain.csproj -p:ToolchainRid=win-x64 -o artifacts/packages
```

`fetch.cs` keeps its downloads in a cache keyed by hash, so a second run downloads nothing.
NASM is built from source on Linux and macOS, which needs a C compiler and `make` on that
machine, of the package's CPU: the `linux-arm64` package is staged on an ARM64 machine.

## Licenses

| Component | License | In the package |
|---|---|---|
| NASM | BSD-2-Clause | `tools/LICENSE.nasm.txt` |
| LLVM tools | Apache-2.0 WITH LLVM-exception | `tools/LICENSE.llvm.txt` |
| AsmFoundation.Sdk and the packaging | Apache-2.0 | `LICENSE` |

The package license expression is
`BSD-2-Clause AND Apache-2.0 WITH LLVM-exception AND Apache-2.0`.
