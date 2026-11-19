# Runtimes and linking

This page covers how each kind of output is linked on each OS, how a program reaches its C
library without the target's sysroot or SDK, and the link options that matter in production.
Every link line here is the one the 0.3.0 targets produce, with the bundled lld 23.1.5. The
error messages are lld's own. [Platforms and runtime identifiers](platforms.md) covers the
seven RIDs, and how one source serves several of them.


## Choosing a target framework

The target framework says what the code links against, the way `net10.0` says which .NET a
program runs on: `<TargetFramework>libc</TargetFramework>`.

| | `freestanding` (default) | `libc` |
|---|---|---|
| Links against | nothing on Linux and macOS; kernel32 on Windows | the platform's C library, through a stand-in the SDK writes: glibc, musl, the Universal C Runtime (UCRT) or libSystem |
| Executable entry | `_start` (or `StartupObject`), entered directly by the kernel or the Windows loader | `main`, called by the SDK's startup code (Linux, Windows) or by dyld (macOS) |
| Executable type | Linux: static, no interpreter. Windows: a console `.exe` that imports from kernel32 alone. macOS: none (**ASM0015**) | Linux: a PIE with the C library's loader as interpreter. Windows: a console `.exe`. macOS: a Mach-O program started through `LC_MAIN` |
| I/O, memory, threads | Linux system calls; kernel32 on Windows | anything the C library offers: `printf`, `malloc`, threads, … |
| Runs on | any Linux kernel of its CPU; any Windows | glibc 2.34 or later, musl, Windows 10 or later, macOS 11 or later |
| Typical use | small and fast libraries for P/Invoke, tools without dependencies, kernels of hot loops | programs that need C library services; libraries that call `malloc`, `printf` or sockets through it |

A shared library loaded by .NET or Python can be `freestanding` even though its host process
uses a C library: it only has to avoid calling one. Choose `libc` the moment the library calls
a C library function: the stand-in is what gives the link its dependency on the library and
the import stubs.

**Both:** `<TargetFrameworks>freestanding;libc</TargetFrameworks>` builds the project once for
each, like a multi-targeted `.csproj`, into `bin/<Configuration>/freestanding/<rid>/` and
`bin/<Configuration>/libc/<rid>/`. The assembler gets `FREESTANDING` or `LIBC` defined, so one
source can use the C library where it has it (`%ifdef LIBC`) and go to the OS directly
elsewhere. NuGet then gives each consumer the nearest build: a `libc` or .NET project gets the
`libc` one, a `freestanding` project the `freestanding` one. A `freestanding` project cannot
reference `libc`-only code at all: restore fails with `NU1201` (a project) or `NU1202` (a
package).

## The exact link lines

The bundled `lld` is one program with three drivers: `lld -flavor gnu` (ld.lld) for Linux,
`lld -flavor link` (lld-link) for Windows, and `lld -flavor darwin` (ld64.lld) for macOS. Each
link ends with the OS's common arguments, then the `ExportsFile` arguments, then the
`LinkerArg` items, so your own arguments come last.

**Linux** (ld.lld). Common arguments: `-z noexecstack --build-id=sha1`.

| Output | Command (abridged) |
|---|---|
| Shared library | `-shared -soname lib<name>.so -Bsymbolic -z relro -z now --hash-style=gnu -o lib<name>.so <objects> <link inputs> [libasmsdk_nonshared.a --no-as-needed <C library stub>]` |
| freestanding executable | `-static -e <StartupObject or _start> -o <name> <objects> <link inputs>` |
| libc executable | `-pie --dynamic-linker <loader> -z relro -z now --hash-style=gnu -e _start -o <name> crt1.o <objects> <link inputs> libasmsdk_nonshared.a --no-as-needed <C library stub>` |

The C library stub is `libc.so.6` (glibc) or `libc.musl-x86_64.so.1` (musl). The loader is
`/lib64/ld-linux-x86-64.so.2` (linux-x64), `/lib/ld-linux-aarch64.so.1` (linux-arm64) or
`/lib/ld-musl-x86_64.so.1` (linux-musl-x64). `AsmDynamicLinker` overrides the loader.

**Windows** (lld-link). Common arguments: `-nodefaultlib -machine:x64` (or `arm64`) `-Brepro`,
plus `-debug` when debug symbols are on.

| Output | Command (abridged) |
|---|---|
| DLL | `-dll -noentry -out:<name>.dll -implib:<name>.dll.lib <objects> <link inputs> [asmsdk_winstdio.lib ucrtbase.lib] kernel32.lib` |
| Executable | `-subsystem:console -entry:<entry> -out:<name>.exe -noimplib [wincrt.obj] <objects> <link inputs> [asmsdk_winstdio.lib ucrtbase.lib] kernel32.lib` |

The entry is `_start` or `StartupObject` for a `freestanding` program, and `mainCRTStartup`
for a `libc` one. A program with an `ExportsFile` gets `-implib:<name>.lib` instead of
`-noimplib`.

**macOS** (ld64.lld). Common arguments: `-arch x86_64` (or `arm64`)
`-platform_version macos 11.0 11.0`, from `AsmMacOSMinimumVersion`.

| Output | Command (abridged) |
|---|---|
| Shared library | `-dylib -install_name @rpath/lib<name>.dylib -o lib<name>.dylib <objects> <link inputs> [libSystem.tbd]` |
| Executable (`libc` only) | `-execute [-e _<StartupObject>] -o <name> <objects> <link inputs> libSystem.tbd` |

**Static libraries**, on every OS: the archive is deleted, then
`llvm-ar rcsD --format=gnu|coff|darwin <archive> <objects>`, in the OS's own archive format.

**What the flags buy:**

- **`-z relro -z now`:** every symbol is bound at load time, and the GOT is then made
  read-only. A missing symbol therefore fails when the library loads, not in the middle of
  a run.
- **`-Bsymbolic`:** a Linux shared library's references to its own global symbols bind to the
  library itself, as they do on Windows and macOS. Without it, `lea rax, [rel global_table]`
  and an ARM64 `adrp` to a global symbol fail to link ("recompile with -fPIC"). A library that
  wants ELF's interposition back adds `<LinkerArg Include="-Bno-symbolic" />`.
- **Reproducible output:** `--build-id=sha1` on Linux and `-Brepro` on Windows derive the id
  and the timestamp from the content. Together with `llvm-ar`'s `D` (zero timestamps and ids),
  identical inputs give byte-identical outputs. The inputs include each source's absolute
  path, which NASM records in the symbol table (see [CI and releases](ci-and-release.md)).
- **`--no-as-needed`:** keeps `DT_NEEDED` on the C library even when only a linked library
  calls into it.
- **`--hash-style=gnu`:** the GNU hash table, which glibc's loader looks up fastest.
- **`-nodefaultlib`:** lld-link reads no library the objects merely ask for. The SDK names every
  input itself, kernel32 and the UCRT included.
- **`-noentry`:** a DLL runs no code when it loads (no `DllMain`).
- **`@rpath/` install name:** a program that links the dylib finds it through its own run
  paths, the convention for libraries shipped with an app. ld64.lld also signs arm64 output ad
  hoc, which Apple silicon requires of every binary.
- **Static library:** deleting the archive first means objects of removed sources do not
  linger in it.

**Order:** your objects come first, then `AsmLinkLibrary` items in the order added, so
references and packages come after anything you list yourself, then the system libraries.
The lld drivers resolve symbols from archives regardless of order, unlike GNU `ld`, so the
order matters only with `AsmToolchain=System` on Linux.

## Freestanding programs

**Linux.** The kernel enters `_start` with this stack:

- `rsp` (`sp` on ARM64) points at `argc`;
- then `argv[0..argc-1]` and a `NULL`;
- then `envp[]` and a `NULL`;
- then the auxiliary vector.

The stack pointer is 16-byte aligned at entry. There is no return address: never `ret` from
`_start`. End with the `exit_group` system call: number 231 with `syscall` on x64, 94 with
`svc #0` on ARM64 (whose system call numbers are the generic table's: `write` is 64, not 1).

```nasm
; start.linux.asm: system calls are Linux-only, so the file says so
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

**Windows.** Windows system call numbers change between releases, so a program calls kernel32,
which every Windows output links. The loader calls `_start` like any function: `rsp` is
`16n+8`, and the callee owns the 32 bytes above the return address. End with `ExitProcess`:

```nasm
; start.win.asm
default rel
%include "asmsdk.inc"
extern GetStdHandle, WriteFile, ExitProcess

section .rodata
message:        db "hello from kernel32", 13, 10
message_len     equ $ - message

section .text
FUNCTION _start
    sub     rsp, 56                 ; 16-aligned: shadow space, argument 5, the byte count
    mov     ecx, -11                ; STD_OUTPUT_HANDLE
    call    GetStdHandle
    mov     rcx, rax                ; the handle
    lea     rdx, [message]
    mov     r8d, message_len
    lea     r9, [rsp + 40]          ; DWORD *written
    mov     qword [rsp + 32], 0     ; argument 5: no OVERLAPPED
    call    WriteFile
    xor     ecx, ecx                ; exit code 0
    call    ExitProcess             ; never returns
ENDFUNCTION
```

The import library adds an import only for the functions a program calls: this one imports
`GetStdHandle`, `WriteFile` and `ExitProcess`, and nothing else.

**macOS** has no freestanding programs: Apple supports no static executables, and dyld starts
every program, with libSystem. A freestanding *library* is fine.

**Linking other code:**

- **A Linux freestanding executable is linked `-static`, so every input must be an object or an
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

- **The program defines `main`,** an ordinary `int main(int argc, char **argv)`. What calls it
  depends on the OS:
  - **Linux:** the SDK's `crt1.o` comes first on the link line. Its `_start` does what glibc's
    `crt1.o` does: it calls `__libc_start_main(main, argc, argv, NULL, NULL, rtld_fini,
    stack_end)` through the GOT. musl's takes the same arguments. glibc 2.34+ runs
    `.init_array` and `.fini_array` itself, and passes `main`'s return value to `exit()`,
    which flushes `stdio`.
  - **Windows:** the SDK's `mainCRTStartup` (`wincrt.obj`) does what the MSVC runtime's does
    for a C program, reduced to what assembly needs. It tells the UCRT it runs a console app,
    builds the narrow `argv` and environment, calls `main(argc, argv, envp)`, and passes the
    result to `exit()`, which runs the `atexit` handlers and flushes `stdio`.
  - **macOS:** dyld loads libSystem and calls `main` (`LC_MAIN`). Returning from `main` exits.
- **`main` follows the platform's calling convention.** On x64 it is entered with
  `rsp ≡ 8 (mod 16)`, so align the stack before calling anything. On Windows, also reserve
  `SHADOW_SPACE`. In a System V variadic call (`printf`), `al` holds the number of vector
  registers used (`xor eax, eax` when there are none). On ARM64 macOS, every variadic argument
  goes on the stack. [Platforms](platforms.md#writing-code-that-runs-everywhere) has the
  macros that absorb the rest.
- **Call the C library through the PLT on Linux:** `call printf PLT` with `asmsdk.inc` (or
  `wrt ..plt` by hand), `CALL printf` with `asmsdk-gnu.inc`. A PIE links a plain `call printf`
  too (the linker adds the PLT entry), but a shared library does not. The macros keep code
  portable between the two, and between the OSes.
- **Data from the C library** has a different name on each OS. `stdout` is a data object on
  Linux, `__stdoutp` on macOS, and the result of `__acrt_iob_func(1)` on Windows. On Linux:
  - In a PIE, `mov rdi, [rel stdout]` works through a copy relocation.
  - In a shared library, go through the GOT: `mov rax, [rel stdout wrt ..got]` then
    `mov rdi, [rax]`.
- **Your own entry:** setting `StartupObject` on a `libc` executable drops the SDK's startup
  code (Linux, Windows). Your entry must then initialize the C library itself: on Linux by
  calling `__libc_start_main`. Without that, `printf` and `malloc` are used before the C
  library is ready. On macOS, `StartupObject` names the function dyld calls instead of `main`.

## The C library stand-ins

The SDK links each platform's C library against a stand-in it writes itself, from the symbol
lists in its `stubs/` folder. Linking against it gives the output its dependency on the real
library and the import stubs for the functions it calls. At run time, the real library
supplies every symbol, and nothing of the stand-in runs. That is why a `libc` program builds on
any build machine, without the target's sysroot or SDK.

| Platform | Symbol list | What the SDK writes |
|---|---|---|
| Linux (glibc, musl) | `linux.symbols`: 292 functions and 4 data objects (`stdin`, `stdout`, `stderr`, `environ`) | a stub shared library named `libc.so.6` or `libc.musl-x86_64.so.1`, assembled with NASM (x64) or llvm-mc (ARM64), and `libasmsdk_nonshared.a` |
| macOS | `macos.symbols`: 257 functions (with `dyld_stub_binder`) and 4 data objects | `libSystem.tbd`, a text stub with the install name `/usr/lib/libSystem.B.dylib`, the format of Apple's own SDK stubs |
| Windows | `ucrtbase.symbols`: the 2,484 C exports of `ucrtbase.dll` | `ucrtbase.lib`, an import library, and `asmsdk_winstdio.lib` |
| Windows, every output | `kernel32.symbols`: the 1,693 exports of `kernel32.dll` | `kernel32.lib`, an import library |

Two archives supply what a C compiler takes from the runtime's static archives:

- **`libasmsdk_nonshared.a`** (Linux): `atexit`, which glibc keeps in `libc_nonshared.a` rather
  than in `libc.so.6`. In 0.2.0, a program that called it linked but did not start.
- **`asmsdk_winstdio.lib`** (Windows): `printf`, `fprintf`, `sprintf`, `snprintf`, their `v`
  forms, `sscanf` and `atexit`. The UCRT headers define those functions inline, over
  `__stdio_common_vfprintf`, `__stdio_common_vsprintf` and `__stdio_common_vsscanf`, and
  `atexit` as `_crt_atexit`, so `ucrtbase.dll` exports none of them. The wrappers are linked
  into every Windows `libc` output and exported from none.

**The Linux list**, grouped as in the file:

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

The macOS list has the same groups, with macOS's names: `__error` for errno, `kqueue` and
`kevent` instead of `epoll`, no `sigwaitinfo`, and no pthread spinlocks or barriers.

**A function that is not listed:**

| Output | Result | Fix |
|---|---|---|
| `Exe` | `lld : error : undefined symbol: strsignal`, with the referencing object and source line | `<AsmLibcSymbol Include="strsignal" />` |
| Linux shared library | links, leaving `strsignal` as an undefined dynamic symbol that the C library satisfies at load time (with `-z now`) | nothing, unless you link with `-z defs`; then add the `AsmLibcSymbol` item too |
| Windows DLL, macOS dylib | `undefined symbol`, as for a program: neither format leaves symbols to be found at load time | `<AsmLibcSymbol Include="strsignal" />` |

`AsmLibcSymbol` takes the C name on every OS: the SDK adds macOS's underscore. On Windows it
adds the name to the UCRT import library, so the name must be a real `ucrtbase.dll` export.
A function that exists only in another DLL needs that DLL's import library, as an
`AsmLinkLibrary` item.

**Versions and limits:**

- **The Linux stub's symbols are unversioned.** An unversioned reference binds to glibc's
  default version, the one a C program compiled today would use (for example
  `memcpy@@GLIBC_2.14`).
- **`pthread_*` and `dl*` are listed as `libc.so.6` symbols,** which they are since glibc
  2.34. Older systems (Ubuntu 20.04, Debian 11, RHEL 8) keep them in `libpthread.so.0` and
  `libdl.so.2`, so programs that use them fail to load there. musl has always kept them in
  its C library.
- **One Linux list serves glibc and musl.** A name only one of them has links for both, and
  fails at load time on the other, with the loader naming it.
- **Data objects matter for size:** on Linux, a copy relocation copies exactly `st_size`
  bytes. The four data objects are 8-byte pointers, as in glibc and musl. To use other C
  library data, write a C shim, or take its address at run time with `dlsym`.
- **osx-x64's `stat` family** is the old 32-bit-inode one. Apple's headers redirect C code to
  `stat$INODE64` and its friends, which assembly must name itself. osx-arm64 has one version
  only.

**Example:** [WordCount](../examples/wordcount.md) calls `warn` and `warnx`, which the Linux list does not include, by adding them as `AsmLibcSymbol` items, and reads `stderr`, a libc data object, through the GOT.

## Production link options

Add these as `LinkerArg` items, NativeAOT's name for extra linker arguments. Add them globally
from `Directory.Build.props` when they suit every project. Each OS's linker has its own syntax,
so condition each item on the RID:

```xml
<ItemGroup>
  <LinkerArg Include="-z defs" Condition="$(RuntimeIdentifier.StartsWith('linux-'))" />
  <LinkerArg Include="--exclude-libs ALL" Condition="$(RuntimeIdentifier.StartsWith('linux-'))" />
  <LinkerArg Include="-dead_strip" Condition="$(RuntimeIdentifier.StartsWith('osx-'))" />
</ItemGroup>
```

Write the linker's own syntax: NativeAOT hands its `LinkerArg` items to clang, as `-Wl,…`,
but AsmSdk runs the linker directly.

| Option | Linker | Effect | When |
|---|---|---|---|
| `-z defs` | ld.lld | an undefined symbol in a shared library fails the link, instead of failing when the library loads | every shared library whose imports are all known at link time; with `libc`, list extra C library functions as `AsmLibcSymbol` items. lld-link and ld64.lld always behave this way |
| `--exclude-libs ALL` | ld.lld | symbols that come from linked archives are not exported from your `.so` | a `.so` that embeds a static dependency, such as a package's `.a`, and should export only its own API |
| `-rpath $ORIGIN` | ld.lld | `DT_RUNPATH` makes the loader also look in the library's own folder | a `.so` that needs another `.so` shipped next to it |
| `-rpath @loader_path` | ld64.lld | the same for a dylib: the loader also looks in its folder | a dylib that needs another dylib shipped next to it |
| `<ExportsFile>file.map</ExportsFile>` | all | a property, NativeAOT's name: it controls exactly which symbols are exported (and, on Linux, their versions). Editing the file relinks | a public C ABI with a compatibility promise. See [Exports](platforms.md#exports) |
| `--gc-sections`, `-opt:ref`, `-dead_strip` | ld.lld, lld-link, ld64.lld | drops unreferenced sections | only with one function per section (`section .text.fn`), which NASM sources rarely use |
| `--strip-debug` / `-s` | ld.lld | removes DWARF / all symbols that are not dynamic | Release outputs where size matters; keep an unstripped copy for debugging |

**Quoting `$ORIGIN`:** MSBuild runs the link command through `cmd.exe` on Windows and
through `/bin/sh` on Linux and macOS. `/bin/sh` expands `$ORIGIN` to an empty string, so the
item differs per build machine:

```xml
<ItemGroup Condition="$(RuntimeIdentifier.StartsWith('linux-'))">
  <LinkerArg Include="-rpath $ORIGIN" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
  <LinkerArg Include="-rpath '$ORIGIN'" Condition="!$([MSBuild]::IsOSPlatform('Windows'))" />
</ItemGroup>
```

Check the result with [`inspect_elf.py`](../ecosystem/claude-code.md#inspect_elfpy), which reports `RUNPATH: $ORIGIN`.

**A shared library that links another shared library** (a `ProjectReference` to a plain
`Library`, or a package with `AsmPackageLinkKind=Shared`) depends on it by name:
`DT_NEEDED lib<other>.so` on Linux, an import of `<other>.dll` on Windows (through the
`.dll.lib` import library), `@rpath/lib<other>.dylib` on macOS. The loader must then find
that file at run time:

- in the application's folder, which is where .NET copies both files, and where Windows looks
  first;
- with `LD_LIBRARY_PATH` (Linux), or through `RUNPATH` / `@rpath` entries;
- in the system folders.

Static linking avoids the question.

## Debug information and stripping

- **Debug** (`DebugSymbols=true`, which Microsoft.NET.Sdk sets for every Debug build):
  - **Linux, macOS:** NASM writes DWARF (`-g -F dwarf`) and llvm-mc writes DWARF (`-g`) into
    the objects, and the linker keeps it in the output. Debug with `gdb` (`break hello_add`,
    `layout asm`, `info registers`) or `lldb` (`b hello_add`, `register read`).
  - **Windows:** NASM writes CodeView (`-g -F cv8`), and lld-link writes `<name>.pdb` beside
    the output (`-debug`), for Visual Studio, WinDbg or Rider. Objects from `.s` files carry
    no line information on Windows, because llvm-mc writes DWARF only. The PDB still names
    their functions.

  `<DebugType>none</DebugType>` turns it off.
- **Release:** no debug information, and `RELEASE` instead of `DEBUG`.
  - `<DebugSymbols>true</DebugSymbols>` adds debug information to a Release build. A .NET
    Release build writes a PDB by default; an asm one stays free of debug information unless
    asked.
  - The symbol table remains, which keeps profiles readable.
  - On Linux, add `<LinkerArg Include="-s" Condition="'$(Configuration)' == 'Release'" />` to
    shrink the output further.
- **Exported symbols:** on ELF, `global name:function` gives the type, and
  `global name:function (name.end - name)` with a local `.end:` label gives the size.
  Profilers (`perf`) and symbolizers attribute samples correctly only with both. `FUNCTION`
  gives the type. `asmsdk-gnu.inc`'s `ENDFUNCTION` also gives the size.

## Platform limits

- **Running:** every build machine builds every RID. Each one runs its own platform's
  outputs, plus the x64 ones under emulation on Windows on Arm and on Apple silicon (Rosetta 2).
  Linux outputs built on Windows or macOS run in a container, or in WSL.
- **Static glibc executables** need glibc's real archives and start files, which the
  bundled toolchain does not have. Use `AsmToolchain=System` on a Linux build machine with
  glibc's development files, and pass your own link line through `LinkerArg` items or an
  extension target (see [Extending the build](extending.md)).
- **musl on ARM64** (`linux-musl-arm64`) is not one of the seven RIDs. A .NET app built for it
  gets a project's `linux-arm64` build through the RID graph. That build loads into a .NET
  process on Alpine only when it is `freestanding`.
- **CPU features:** the SDK does not restrict instructions. Code that uses AVX2, AVX-512 or
  ARM64 extensions beyond the baseline must check for them at run time (CPUID on x64; on
  ARM64, the ID registers on Linux, `IsProcessorFeaturePresent` on Windows, `sysctlbyname`
  on macOS), or document the requirement.
