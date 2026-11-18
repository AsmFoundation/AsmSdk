# Third-party notices

The `AsmFoundation.Sdk` package contains only this repository's own files
(Apache-2.0).

The `AsmFoundation.Toolchain.<rid>` packages also redistribute unmodified
tools. Their license texts travel inside each package, in `tools/`:

| Component | Version | License | File in the package |
|---|---|---|---|
| [NASM](https://www.nasm.us) (`nasm`) | 3.02 | BSD-2-Clause | `tools/LICENSE.nasm.txt` |
| [LLVM](https://llvm.org) (`lld`, `llvm-ar`, `llvm-readobj`) | 23.1.5 | Apache-2.0 WITH LLVM-exception | `tools/LICENSE.llvm.txt` |

**Where the binaries come from:**

- **NASM on Windows:** the official `nasm-3.02-win64.zip`.
- **NASM on Linux and macOS:** built, unmodified, from the official
  `nasm-3.02.tar.xz`. The Linux build is linked statically.
- **The LLVM tools:** taken unmodified from the official LLVM 23.1.5 release
  archives on GitHub.

Every source archive is pinned by SHA-256 in
[toolchain/versions.json](toolchain/versions.json), and the build verifies it
before use.

Programs built with AsmFoundation.Sdk contain none of these tools. A program
built for `TargetFramework=libc` links glibc **dynamically** at run time: it ships
no glibc code.
