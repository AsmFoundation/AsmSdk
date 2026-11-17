# AsmFoundation.Toolchain

The assembler and linker that [AsmFoundation.Sdk](https://www.nuget.org/packages/AsmFoundation.Sdk)
runs to build `.asmproj` projects. There is one package per build machine:

- `AsmFoundation.Toolchain.win-x64`
- `AsmFoundation.Toolchain.linux-x64`
- `AsmFoundation.Toolchain.osx-arm64`

All of them produce the same output: Linux x86-64 ELF objects, libraries and
executables.

You do not reference this package yourself. The SDK downloads the one that
matches your machine during restore.

## Contents

All of the tools are in `tools/`:

| Tool | From | License |
|---|---|---|
| `nasm` | [NASM](https://www.nasm.us) (official Windows binary; built from the official source on Linux, statically, and on macOS, natively for arm64) | BSD-2-Clause (`LICENSE.nasm.txt`) |
| `lld` (run as `lld -flavor gnu`, i.e. `ld.lld`) | [LLVM](https://llvm.org) official release | Apache-2.0 WITH LLVM-exception (`LICENSE.llvm.txt`) |
| `llvm-ar` | LLVM official release | Apache-2.0 WITH LLVM-exception |
| `llvm-readobj` (also answers as `llvm-readelf`) | LLVM official release | Apache-2.0 WITH LLVM-exception |

`VERSIONS.txt` records the exact versions. Every download behind this package
is pinned by SHA-256 in
[toolchain/versions.json](https://github.com/AsmFoundation/AsmSdk/blob/main/toolchain/versions.json),
and [toolchain/fetch.py](https://github.com/AsmFoundation/AsmSdk/blob/main/toolchain/fetch.py)
verifies each hash before it opens the file.
