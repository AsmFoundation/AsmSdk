# Runtimes and linking

This page covers how each kind of output is linked, how a program reaches glibc without a
sysroot, and the link options that matter in production. Every link line and every error
message here is the one the 0.2.0 targets and the bundled `ld.lld` 23.1.5 produce.


## Choosing a target framework

The target framework says what the code links against, the way `net10.0` says which .NET a
program runs on: `<TargetFramework>libc</TargetFramework>`.

| | `freestanding` (default) | `libc` |
|---|---|---|
| Links against | nothing | glibc, dynamically, through a generated `libc.so.6` stub |
| Executable entry | `_start` (or `StartupObject`); the kernel jumps there directly | `main`, called by the SDK's `crt1` through `__libc_start_main` |
| Executable type | static (`ET_EXEC`), no interpreter | PIE (`ET_DYN` with `DF_1_PIE`), `PT_INTERP /lib64/ld-linux-x86-64.so.2` |
| Shared library `DT_NEEDED` | none | `libc.so.6` |
| I/O, memory, threads | your own system calls | anything glibc offers: `printf`, `malloc`, `pthread_*`, `dlopen`, … |
| Runs on | any x86-64 Linux kernel | glibc 2.34 or later |
| Typical use | small and fast libraries for P/Invoke, tools without dependencies, kernels of hot loops | programs that need libc services; libraries that call `malloc`, `printf` or sockets through libc |

A shared library loaded by .NET or Python can be `freestanding` even though its host
process uses glibc: it only has to avoid calling libc. Choose `libc` the moment the
library calls a libc function, because the stub is what gives the link `DT_NEEDED` and the
PLT entries.

**Both:** `<TargetFrameworks>freestanding;libc</TargetFrameworks>` builds the project once
for each, like a multi-targeted `.csproj`, into `bin/<Configuration>/freestanding/` and
`bin/<Configuration>/libc/`. NASM gets `-DFREESTANDING` or `-DLIBC`, so one source can use
glibc where it has it (`%ifdef LIBC`) and its own system calls elsewhere. NuGet then gives
each consumer the nearest build: a `libc` or .NET project gets the `libc` one, a
`freestanding` project the `freestanding` one. A `freestanding` project cannot reference
`libc`-only code at all: restore fails with `NU1201` (a project) or `NU1202` (a package).

## The exact link lines

Every link appends `-z noexecstack --build-id=sha1`, then the `ExportsFile` arguments and
the `LinkerArg` items, so your own arguments come last. The linker is `lld -flavor gnu`
(bundled) or `ld` (`AsmToolchain=System`).

| Output | Command (abridged) |
|---|---|
| Shared library | `-shared -soname lib<name>.so -z relro -z now --hash-style=gnu -o lib<name>.so <objects> <AsmLinkLibrary…> [--no-as-needed libc.so.6(stub)]` |
| freestanding executable | `-static -e <StartupObject or _start> -o <name> <objects> <AsmLinkLibrary…>` |
| libc executable | `-pie --dynamic-linker /lib64/ld-linux-x86-64.so.2 -z relro -z now --hash-style=gnu -e _start -o <name> crt1.o <objects> <AsmLinkLibrary…> --no-as-needed libc.so.6(stub)` |
| Static library | the archive is deleted, then `llvm-ar rcsD lib<name>.a <objects>` |

**What the flags buy:**

- **`-z relro -z now`:** every symbol is bound at load time, and the GOT is then made
  read-only. A missing symbol therefore fails when the library loads, not in the middle of
  a run.
- **`--build-id=sha1`:** the id is computed from the content. Together with `llvm-ar`'s
  `D` (zero timestamps and ids), identical inputs give byte-identical outputs. The
  inputs include each source's absolute path, which NASM records in the symbol table (see
  [CI and releases](ci-and-release.md)).
- **`--no-as-needed`:** keeps `DT_NEEDED libc.so.6` even when only a linked library calls
  into libc.
- **`--hash-style=gnu`:** the GNU hash table, which glibc's loader looks up fastest.
- **Static library:** deleting the archive first means objects of removed sources do not
  linger in it.

**Order:** your objects come first, then `AsmLinkLibrary` items in the order added, so
references and packages come after anything you list yourself. `ld.lld` resolves symbols
from archives regardless of order, unlike GNU `ld`, so the order matters only with
`AsmToolchain=System`.

## Freestanding programs

The kernel enters `_start` with this stack:

- `rsp` points at `argc`;
- then `argv[0..argc-1]` and a `NULL`;
- then `envp[]` and a `NULL`;
- then the auxiliary vector.

`rsp` is 16-byte aligned at entry. There is no return address: never `ret` from `_start`.
End with the `exit_group` system call (number 231).

```nasm
default rel
section .text
global _start:function
_start:
    mov     rdi, [rsp]          ; argc
    lea     rsi, [rsp + 8]      ; argv
    ; ... work ...
    mov     eax, 231            ; exit_group
    xor     edi, edi            ; status 0
    syscall
section .note.GNU-stack noalloc noexec nowrite progbits
```

**Linking other code:**

- **A freestanding executable is linked `-static`, so every input must be an object or an
  archive.** A `.so` input fails with `attempted static link of dynamic object <path>`.
  That happens with a `ProjectReference` to a plain `Library`, or a package packed with
  `AsmPackageLinkKind=Shared`.
- **Symbols are resolved at link time:** `undefined symbol: x` names the object and the
  source line.

## libc programs and libraries

```xml
<PropertyGroup>
  <TargetFramework>libc</TargetFramework>
  <OutputType>Exe</OutputType>
</PropertyGroup>
```

- **The program defines `main`.** The SDK's `crt1.o` comes first on the link line. Its
  `_start` does what glibc's `crt1.o` does: it calls
  `__libc_start_main(main, argc, argv, NULL, NULL, rtld_fini, stack_end)` through the GOT.
  glibc 2.34+ runs `.init_array` and `.fini_array` itself, and passes `main`'s return value
  to `exit()`, which flushes `stdio`.
- **`main` follows the System V ABI.** It is entered with `rsp ≡ 8 (mod 16)`, so push one
  register (or `sub rsp, 8`) before calling anything. In each variadic call (`printf`),
  `al` holds the number of vector registers used (`xor eax, eax` when there are none).
- **Call libc through the PLT:** `call printf wrt ..plt`. A PIE links a plain `call printf`
  too (the linker adds the PLT entry), but a shared library does not. Writing `wrt ..plt`
  everywhere keeps code portable between the two.
- **Data from libc:**
  - In a PIE, `mov rdi, [rel stdout]` works through a copy relocation.
  - In a shared library, go through the GOT: `mov rax, [rel stdout wrt ..got]` then
    `mov rdi, [rax]`.
- **Your own entry:** setting `StartupObject` on a `libc` executable drops `crt1.o`. Your
  entry must then initialize glibc itself, by calling `__libc_start_main`. Without that,
  `printf` and `malloc` are used before glibc is ready.

## The libc stub

- **What it is:** `stubs/libc.asm` defines about 290 functions as empty bodies, plus four
  pointer-sized data objects (`stdin`, `stdout`, `stderr`, `environ`). The SDK links it as
  a shared object named `libc.so.6`.
- **What linking against it gives:**
  - the output records `DT_NEEDED libc.so.6`;
  - the linker creates PLT and GOT entries for the functions;
  - at run time, the real glibc defines every symbol.
- **Why a stub:** a libc program builds on Windows and macOS without a Linux sysroot.

**The functions it lists**, grouped as in the source:

- **process start and exit:** `__libc_start_main`, `exit`, `_exit`, `atexit`,
  `__cxa_atexit`, `__cxa_finalize`, `abort`, `__stack_chk_fail`, `__assert_fail`,
  `__errno_location`;
- **memory:** `malloc`, `free`, `calloc`, `realloc`, `reallocarray`, `aligned_alloc`,
  `posix_memalign`, `memalign`, `valloc`, `malloc_usable_size`;
- **strings and memory blocks:** the `mem*`, `str*` and `stp*` families, `strerror`,
  `strerror_r`, `strtol`, `strtoul`, `strtod`, `atoi`, …;
- **stdio:** the `printf` family (including the `v*` variants), `puts`, `fputs`, `fputc`,
  `fwrite`, `fread`, `fopen`, `fclose`, `fflush`, `fgets`, `getline`, `perror`, `setvbuf`,
  `fileno`, `sscanf`;
- **environment and randomness:** `getenv`, `setenv`, `qsort`, `bsearch`, `rand`,
  `random`, `getrandom`, `getentropy`;
- **time:** `clock_gettime`, `nanosleep`, `usleep`, `sleep`, `gettimeofday`, `time`,
  `localtime_r`, `gmtime_r`, `strftime`;
- **threads:** `pthread_create`, `pthread_join`, … plus the mutex, condition variable,
  rwlock, spinlock, key, barrier and affinity functions;
- **signals:** `signal`, `sigaction`, the `sig*set` functions, `sigprocmask`,
  `sigwaitinfo`, `raise`, `kill`;
- **scheduling and system:** `sched_*`, `get_nprocs`, `sysconf`, `getpagesize`, `uname`,
  `prctl`, `syscall`;
- **files:** `open`, `openat`, `close`, `read`, `write`, `pread`, `pwrite`, `readv`,
  `writev`, `lseek`, `fstat`, `stat`, `ftruncate`, `fsync`, `unlink`, `mkdir`, `rename`,
  `dup*`, `pipe*`, `fcntl`, `ioctl`, `mmap`, `munmap`, `mprotect`, `madvise`, `mremap`,
  `memfd_create`, …;
- **sockets:** `socket`, `bind`, `listen`, `accept4`, `connect`, the `send*` and `recv*`
  families including `sendmmsg` and `recvmmsg`, `setsockopt`, `getaddrinfo`, `inet_pton`,
  `htons`, …;
- **event notification:** the `epoll_*` functions, `poll`, `ppoll`, `select`, `eventfd`,
  `timerfd_*`, `signalfd`;
- **processes:** `getpid`, `gettid`, `fork`, `execve`, `waitpid`, `wait`, `getrlimit`,
  `getrusage`, …;
- **dynamic loading:** `dlopen`, `dlsym`, `dlclose`, `dlerror`, `dladdr`,
  `dl_iterate_phdr`.

**A function that is not listed:**

| Output | Result | Fix |
|---|---|---|
| `Exe` | `lld : error : undefined symbol: strsignal`, with the referencing object and source line | `<AsmLibcSymbol Include="strsignal" />` |
| shared library | links, leaving `strsignal` as an undefined dynamic symbol that glibc satisfies at load time (with `-z now`) | nothing, unless you link with `-z defs`; then add the `AsmLibcSymbol` item too |

**Versions and limits:**

- **The stub's symbols are unversioned.** An unversioned reference binds to glibc's
  default version, the one a C program compiled today would use (for example
  `memcpy@@GLIBC_2.14`).
- **`pthread_*` and `dl*` are listed as `libc.so.6` symbols,** which they are since glibc
  2.34. Older systems (Ubuntu 20.04, Debian 11, RHEL 8) keep them in `libpthread.so.0` and
  `libdl.so.2`, so programs that use them fail to load there.
- **Data objects matter for size:** a copy relocation copies exactly `st_size` bytes. The
  four data objects are 8-byte pointers, as in glibc. To use other glibc data, write a C
  shim, or take its address at run time with `dlsym`.

**Example:** [WordCount](../examples/wordcount.md) calls `warn` and `warnx`, which the stub does not list, by adding them as `AsmLibcSymbol` items, and reads `stderr`, a libc data object, through the GOT.

## Production link options

Add these as `LinkerArg` items, NativeAOT's name for extra linker arguments, globally from
`Directory.Build.props` when they suit every project:

```xml
<ItemGroup>
  <LinkerArg Include="-z defs" />
  <LinkerArg Include="--exclude-libs ALL" />
</ItemGroup>
```

Write the linker's own syntax: NativeAOT hands its `LinkerArg` items to clang, as `-Wl,…`,
but AsmSdk runs `ld.lld` directly.

| Option | Effect | When |
|---|---|---|
| `-z defs` | an undefined symbol in a shared library fails the link, instead of failing when the library loads | every shared library whose imports are all known at link time; with `libc`, list extra libc functions as `AsmLibcSymbol` items |
| `--exclude-libs ALL` | symbols that come from linked archives are not exported from your `.so` | a `.so` that embeds a static dependency, such as a package's `.a`, and should export only its own API |
| `-rpath $ORIGIN` | `DT_RUNPATH` makes the loader also look in the library's own folder | a `.so` that needs another `.so` shipped next to it |
| `<ExportsFile>file.map</ExportsFile>` (a property, NativeAOT's name: the SDK passes `--version-script=<file>`) | controls exactly which symbols are exported, and their versions; editing the file relinks | a public C ABI with a compatibility promise |
| `--gc-sections` | drops unreferenced sections | only with one function per section (`section .text.fn`), which NASM sources rarely use |
| `--strip-debug` / `-s` | removes DWARF / all symbols that are not dynamic | Release outputs where size matters; keep an unstripped copy for debugging |

**Quoting `$ORIGIN`:** MSBuild runs the link command through `cmd.exe` on Windows and
through `/bin/sh` on Linux and macOS. `/bin/sh` expands `$ORIGIN` to an empty string, so the flag differs per build machine:

```xml
<ItemGroup>
  <LinkerArg Include="-rpath $ORIGIN" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
  <LinkerArg Include="-rpath '$ORIGIN'" Condition="!$([MSBuild]::IsOSPlatform('Windows'))" />
</ItemGroup>
```

Check the result with [`inspect_elf.py`](../ecosystem/claude-code.md#inspect_elfpy), which reports `RUNPATH: $ORIGIN`.

**A `.so` that links another `.so`** (a `ProjectReference` to a plain `Library`, or a
package with `AsmPackageLinkKind=Shared`) gets `DT_NEEDED lib<other>.so`. The loader must
then find that file at run time:

- with `LD_LIBRARY_PATH`;
- in the system folders;
- through `RUNPATH`;
- or because .NET copied both files next to the program and loads them by name.

Static linking avoids the question.

## Debug information and stripping

- **Debug** (`DebugSymbols=true`, which Microsoft.NET.Sdk sets for every Debug build):
  `-g -F dwarf` puts DWARF line tables and symbols in the objects, and the linker keeps them
  in the output. Debug with `gdb` on Linux: `break hello_add`, `layout asm`,
  `info registers`. `<DebugType>none</DebugType>` turns it off.
- **Release:** no debug information, and `RELEASE` instead of `DEBUG`.
  - `<DebugSymbols>true</DebugSymbols>` adds DWARF to a Release build. A .NET Release
    build writes a PDB by default; an asm one stays free of debug information unless asked.
  - The symbol table remains, which keeps profiles readable.
  - Add `<LinkerArg Include="-s" Condition="'$(Configuration)' == 'Release'" />` to shrink
    the output further.
- **Exported symbols:** `global name:function` gives the type, and
  `global name:function (name.end - name)` with a local `.end:` label gives the size.
  Profilers (`perf`) and symbolizers attribute samples correctly only with both.

## Platform limits

- **Output:** Linux x86-64 ELF only. A Windows or macOS build machine builds it but cannot
  run it: use WSL, a container or a Linux CI job.
- **musl (Alpine):** not supported for `libc`, because the stub names `libc.so.6` and the
  interpreter is glibc's. A `freestanding` program runs on Alpine unchanged.
- **Static glibc executables** need glibc's real archives and start files, which the
  bundled toolchain does not have. Use `AsmToolchain=System` on a Linux build machine with
  glibc's development files, and pass your own link line through `LinkerArg` items or an
  extension target (see [Extending the build](extending.md)).
- **CPU features:** the SDK does not restrict instructions. Code that uses AVX2 or AVX-512
  must check CPUID at run time, or document the requirement.
