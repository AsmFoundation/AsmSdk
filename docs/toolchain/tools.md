# The tools

The four tools in `AsmFoundation.Toolchain`, the exact commands the build runs with them,
and how to run them yourself.

## Where they are

```text
<global packages>/asmfoundation.toolchain.<rid>/<version>/tools/
    nasm[.exe]  lld[.exe]  llvm-ar[.exe]  llvm-readobj[.exe]
    VERSIONS.txt  LICENSE.nasm.txt  LICENSE.llvm.txt

<global packages> = $NUGET_PACKAGES, or ~/.nuget/packages (%USERPROFILE%\.nuget\packages on Windows)
```

For example, after a restore on Windows:
`%USERPROFILE%\.nuget\packages\asmfoundation.toolchain.win-x64\0.1.0\tools\nasm.exe`.

`dotnet build -v:n` prints every command exactly as run. Copy one and run it from the
project folder to experiment.

## NASM 3.02

The build runs NASM once per source, from the project folder:

```text
nasm -f elf64 [-g -F dwarf] -w+all -w-reloc-rel-dword -w-reloc-abs-qword [-w+error] $(AsmFlags)
     -I"<project>/include/" -I"<project>/" -I"<references and packages>/"... -DDEBUG ...
     -o "<project>/obj/Debug/asm/obj/hello.o" "<project>/hello.asm"
```

- `-g -F dwarf` is added in Debug builds, and `-w+error` with `TreatWarningsAsErrors`.
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

## ld.lld 23.1.5

The package's `lld` is LLVM's multi-flavor linker. The build runs it as
`lld -flavor gnu`, which is `ld.lld` with GNU `ld` command-line syntax:

```text
lld -flavor gnu -shared -soname libhello.so -z relro -z now --hash-style=gnu
    -o ".../bin/Debug/libhello.so" ".../obj/Debug/asm/obj/hello.o" [link inputs] [libc stub]
    -z noexecstack --build-id=sha1 $(AsmLinkFlags)
```

The line for each kind of output is in the [reference](../asmproj.md#link-lines). It
differs from GNU `ld` in two ways that matter here:

- **It resolves symbols from archives regardless of their order** on the command line.
- **It refuses text relocations by default.** That is why non-PIC code in a shared library
  fails with `recompile with -fPIC` instead of linking slowly.

## llvm-ar 23.1.5

```text
llvm-ar rcsD ".../bin/Debug/libhello.a" ".../obj/Debug/asm/obj/hello.o" ...
```

`r` replaces members, `c` creates the archive, `s` writes the symbol index that linkers
need, and `D` makes the archive deterministic (zero timestamps, owners and modes). The build
deletes the archive first, so members of removed sources do not linger.

To list an archive's members: `llvm-ar t libhello.a`.

## llvm-readobj 23.1.5

The build does not use it. It is there to check what was built:

| Question | Command |
|---|---|
| Shared object, PIE or static executable? | `llvm-readobj --file-headers --dynamic-table app` (a PIE is `SharedObject` with `FLAGS_1 PIE`) |
| SONAME, NEEDED, RUNPATH | `llvm-readobj --dynamic-table libhello.so` |
| What is exported, with types and sizes? | `llvm-readobj --dyn-syms libhello.so` |
| Is the stack executable? | `llvm-readobj --program-headers libhello.so` (look for `PT_GNU_STACK` without `PF_X`) |
| Which relocations are left for the loader? | `llvm-readobj -r libhello.so` |
| All of it, for a script | `llvm-readobj --elf-output-style=JSON --file-headers --dynamic-table --dyn-syms libhello.so` |

For GNU `readelf`-style output, add `--elf-output-style=GNU`; the same binary copied as
`llvm-readelf` defaults to it. The
[asmsdk Claude Code plugin](../ecosystem/claude-code.md#inspect_elfpy) includes
`inspect_elf.py`. It finds this binary in the package cache by itself and prints one
summary per file. It can also check expectations in CI, such as "exports `hello_add`" or
"needs `libc.so.6`".
