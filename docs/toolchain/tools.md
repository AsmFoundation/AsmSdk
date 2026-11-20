# The tools

The five tools in `AsmFoundation.Toolchain`, the exact commands the build runs with them,
and how to run them yourself.

## Where they are

```text
<global packages>/asmfoundation.toolchain.<rid>/<version>/tools/
    nasm[.exe]  llvm-mc[.exe]  lld[.exe]  llvm-ar[.exe]  llvm-readobj[.exe]
    VERSIONS.txt  LICENSE.nasm.txt  LICENSE.llvm.txt

<global packages> = $NUGET_PACKAGES, or ~/.nuget/packages (%USERPROFILE%\.nuget\packages on Windows)
```

For example, after a restore on Windows:
`%USERPROFILE%\.nuget\packages\asmfoundation.toolchain.win-x64\0.2.0\tools\nasm.exe`.

`dotnet build -v:n` prints every command exactly as run. Copy one and run it from the
project folder to experiment.

## NASM 3.02

The build runs NASM once per `.asm` source of each x64 RID, from the project folder:

```text
nasm -f elf64 [-g -F dwarf] -w+all -w-reloc-rel-dword -w-reloc-abs-qword [-w+error] $(AsmFlags)
     -I"<project>/include/" -I"<project>/" -I"<references and packages>/"... -I"<sdk>/include/"
     -DDEBUG -DFREESTANDING -DLINUX -DX64 ...
     -o "<project>/obj/Debug/freestanding/linux-x64/asm/obj/hello.o" "<project>/hello.asm"
```

- The format is `-f win64` for Windows, with `-D.rodata=.rdata`, and `-f macho64` for macOS,
  with `--gprefix _` (Mach-O's underscore on every global and extern).
- Debug builds add `-g -F dwarf`, or `-g -F cv8` (CodeView) for Windows. `-w+error` comes
  with `TreatWarningsAsErrors`.
- All paths use forward slashes on every OS, because a backslash right before a closing
  quote would escape the quote on Windows.

**A listing shows the machine code next to each line** (the bytes on the home page came
from one):

```sh
nasm -f elf64 -l hello.lst -o hello.o hello.asm
```

```text
     5 00000000 8D0437                      lea     eax, [rdi + rsi]
     6 00000003 C3                          ret
```

**For MSBuild diagnostics,** add `<AsmFlags>-Xvc</AsmFlags>`. NASM then reports
`hello.asm(5): error : …`, which MSBuild counts and IDEs link to.

## llvm-mc 23.1.5

The build runs `llvm-mc` once per `.s` source of each RID, with the RID's target triple:

```text
llvm-mc --triple=aarch64-linux-gnu --filetype=obj [-g] $(AsmLlvmMcFlags)
        -I "<project>/include/" ... -I "<sdk>/include/"
        --defsym=DEBUG=1 --defsym=FREESTANDING=1 --defsym=LINUX=1 --defsym=ARM64=1
        -o "<project>/obj/Debug/freestanding/linux-arm64/asm/obj/hello.o" "<project>/hello.s"
```

| RID | Triple |
|---|---|
| `win-arm64` / `win-x64` | `aarch64-pc-windows-msvc` / `x86_64-pc-windows-msvc` |
| `linux-arm64` / `linux-x64` | `aarch64-linux-gnu` / `x86_64-linux-gnu` |
| `linux-musl-x64` | `x86_64-linux-musl` |
| `osx-arm64` / `osx-x64` | `arm64-apple-macos11.0` / `x86_64-apple-macos11.0` (`AsmMacOSMinimumVersion`) |

- `-g` (DWARF) is added in Debug builds, except for Windows, whose debuggers read PDBs.
- `--defsym` takes integers only: a define `NAME` becomes `NAME=1`, and a non-numeric value
  is llvm-mc's error.

**A listing** shows the encoding of each instruction:
`llvm-mc --triple=aarch64-linux-gnu --show-encoding hello.s`.

## lld 23.1.5

The package's `lld` is LLVM's multi-flavor linker, one program with three drivers. The build
runs it as `lld -flavor gnu` (`ld.lld`, GNU `ld`'s command line) for Linux,
`lld -flavor link` (`lld-link`, Microsoft `link.exe`'s) for Windows, and
`lld -flavor darwin` (`ld64.lld`, Apple `ld`'s) for macOS:

```text
lld -flavor gnu -shared -soname libhello.so -Bsymbolic -z relro -z now --hash-style=gnu
    -o ".../bin/Debug/freestanding/linux-x64/libhello.so" ".../obj/Debug/freestanding/linux-x64/asm/obj/hello.o"
    [link inputs] [C library stub] -z noexecstack --build-id=sha1 [ExportsFile] [LinkerArg items]

lld -flavor link -dll -noentry -out:".../win-x64/hello.dll" -implib:".../win-x64/hello.dll.lib"
    ".../hello.o" [link inputs] [ucrtbase.lib] kernel32.lib -nodefaultlib -machine:x64 -Brepro

lld -flavor darwin -dylib -install_name @rpath/libhello.dylib -o ".../osx-arm64/libhello.dylib"
    ".../hello.o" [link inputs] [libSystem.tbd] -arch arm64 -platform_version macos 11.0 11.0
```

[Runtimes and linking](../guide/runtimes-and-linking.md#the-exact-link-lines) has the line
for each kind of output on each OS. The drivers differ from GNU `ld` in two ways that matter
here:

- **They resolve symbols from archives regardless of their order** on the command line.
- **ld.lld refuses text relocations by default.** That is why non-PIC code in a Linux shared
  library fails with `recompile with -fPIC`. lld-link and ld64.lld accept some fixups their
  loaders cannot apply, which the SDK then reports itself (`ASM0080`, `ASM0081`).

`lld -flavor link -lib -def:<file>.def -machine:x64 -out:<file>.lib` also writes the import
libraries the SDK links Windows outputs against (kernel32, the UCRT).

## llvm-ar 23.1.5

```text
llvm-ar rcsD --format=gnu ".../bin/Debug/freestanding/linux-x64/libhello.a" ".../obj/Debug/freestanding/linux-x64/asm/obj/hello.o" ...
```

`r` replaces members, `c` creates the archive, `s` writes the symbol index that linkers
need, and `D` makes the archive deterministic (zero timestamps, owners and modes). The format
is the OS's: `gnu` for Linux, `coff` for Windows (`hello.lib`), `darwin` for macOS. The build
deletes the archive first, so members of removed sources do not linger.

To list an archive's members: `llvm-ar t libhello.a`.

## llvm-readobj 23.1.5

The build does not use it. It is there to check what was built, in every format:

| Question | Command |
|---|---|
| Which format and CPU? | `llvm-readobj --file-headers <file>` (`Format: COFF-ARM64`, `elf64-x86-64`, `Mach-O arm64`, …) |
| What does a DLL export and import? | `llvm-readobj --coff-exports --coff-imports hello.dll` |
| What does a dylib export, and which libraries does it load? | `llvm-readobj --symbols --needed-libs libhello.dylib` (`Extern` marks an export) |
| Shared object, PIE or static executable? | `llvm-readobj --file-headers --dynamic-table app` (a PIE is `SharedObject` with `FLAGS_1 PIE`) |
| SONAME, NEEDED, RUNPATH | `llvm-readobj --dynamic-table libhello.so` |
| What is exported, with types and sizes? | `llvm-readobj --dyn-syms libhello.so` |
| Is the stack executable? | `llvm-readobj --program-headers libhello.so` (look for `PT_GNU_STACK` without `PF_X`) |
| Which relocations are left for the loader? | `llvm-readobj -r libhello.so` |
| All of it, for a script | `llvm-readobj --elf-output-style=JSON --file-headers --dynamic-table --dyn-syms libhello.so` |

For GNU `readelf`-style output, add `--elf-output-style=GNU`; the same binary copied as
`llvm-readelf` defaults to it. The
[asmsdk Claude Code plugin](../ecosystem/claude-code.md#inspect_outputpy) includes
`inspect_output.py`. It finds this binary in the package cache by itself and prints one
summary per file. It can also check expectations in CI, such as "exports `hello_add`" or
"needs `libc.so.6`".
