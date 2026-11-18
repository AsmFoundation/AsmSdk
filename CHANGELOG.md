# Changelog

All notable changes to AsmFoundation.Sdk and the AsmFoundation.Toolchain
packages. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The toolchain packages carry their own version (`toolchain/versions.json`).
Each SDK release names the one it was tested with.

## [Unreleased]

### Added

- **The documentation site,** <https://asmfoundation.github.io/AsmSdk/>: guides, the
  complete `.asmproj` reference, the toolchain and the ecosystem. It is built with DocFX
  from `docs/`, with DocFX pinned in `.config/dotnet-tools.json`, and deployed to GitHub
  Pages by `.github/workflows/docs.yml`.

### Changed

- **`docs/asmproj.md` is now the complete reference:** evaluation order, every item and its
  metadata, link lines, the libc stub, and generated files.
- **Two corrections in it:**
  - NASM's messages become MSBuild errors with file and line only with `-Xvc`. `lld`'s
    messages always do.
  - A function missing from the libc stub fails executables, not shared libraries.
- **The package's project URL** points at the documentation site, from the next release.

### Fixed

- **The build targets' comment on `reloc-abs-qword`** said that a shared object always
  resolves a `dq` of a label at load time. That holds only in a writable section: `.data`,
  or `.data.rel.ro` declared `write`. In `.rodata`, or in any section without the write
  flag, the link of a shared library or a Libc executable fails. The comment now says so,
  and explains why the warning can stay off: the linker rejects every case that cannot
  work. Only the comment changed; the build does the same.
- **The guide's jump-table rule** now covers Libc executables, which are PIEs, as well as
  shared libraries. It also says that a Freestanding executable, linked `-static`, accepts
  a `dq` of a label in any section.

## [0.1.0] — 2026-11-17

The first release.

### Added

- **`.asmproj` projects** (`<Project Sdk="AsmFoundation.Sdk">`). NASM sources
  become a Linux x86-64 shared library, static library or executable, or just
  objects, chosen with the csproj-style `OutputType`.
- **Two runtimes:**
  - `Freestanding` (the default): raw system calls, nothing linked;
  - `Libc`: glibc linked dynamically through a generated `libc.so.6` stub, so
    no sysroot is needed. A `Libc` executable starts in `main` through the
    SDK's `crt1`.
- **References:**
  - `ProjectReference` between `.asmproj` projects adds the include folders and
    links the library;
  - `ProjectReference` from a `.csproj` copies the shared library next to the
    program;
  - `PackageReference` to an asm package adds the package's include folders and
    library.
- **`dotnet pack`** writes native packages:
  - `include/`;
  - `native/linux-x64/*.a`;
  - `runtimes/linux-x64/native/*.so`;
  - optional `src/`;
  - generated `build/` and `buildTransitive/` props.
- **Exact incremental builds:** a rebuild without changes starts no tool; an
  include file change reassembles the sources that can include it.
- **`AsmFoundation.Toolchain` 0.1.0** for `win-x64`, `linux-x64` and
  `osx-arm64`:
  - NASM 3.02: the official binary on Windows, built from source on Linux
    (statically) and on macOS (arm64);
  - LLVM 23.1.5's `lld`, `llvm-ar` and `llvm-readobj`.

  Every download is pinned by SHA-256 and verified before use.
- **Samples and an end-to-end test** (`tests/run.py`) that CI runs on Windows,
  Linux and macOS.
  - It checks every output's ELF properties: type, SONAME, `DT_NEEDED`,
    interpreter, exports and archive members.
  - It runs the executables and a C# program that P/Invokes two asm shared
    libraries.
  - It verifies the incremental builds.

[0.1.0]: https://github.com/AsmFoundation/AsmSdk/releases/tag/v0.1.0
