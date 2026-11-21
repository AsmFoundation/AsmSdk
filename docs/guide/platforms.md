# Platforms and runtime identifiers

From 0.3.0, one `.asmproj` builds for seven platforms, named the way .NET names them: by
runtime identifier (RID). Any build machine builds any of them. This page covers the RIDs, how
a project chooses them, how its sources and calls differ per platform, and what each platform's
output looks like. Everything here was measured with the 0.3.0 targets, NASM 3.02 and LLVM
23.1.5.

| RID | Object format, linker | Shared library | Static library | Executable | C library (`libc`) |
|---|---|---|---|---|---|
| `win-x64` | PE/COFF, lld-link | `name.dll` (+ `name.dll.lib`, `name.pdb`) | `name.lib` | `name.exe` | Universal C Runtime (`ucrtbase.dll`) |
| `win-arm64` | PE/COFF, lld-link | `name.dll` (+ `name.dll.lib`) | `name.lib` | `name.exe` | Universal C Runtime |
| `linux-x64` | ELF, ld.lld | `libname.so` | `libname.a` | `name` | glibc 2.34+ (`libc.so.6`) |
| `linux-arm64` | ELF, ld.lld | `libname.so` | `libname.a` | `name` | glibc 2.34+ (`libc.so.6`) |
| `linux-musl-x64` | ELF, ld.lld | `libname.so` | `libname.a` | `name` | musl (`libc.musl-x86_64.so.1`): Alpine |
| `osx-x64` | Mach-O, ld64.lld | `libname.dylib` | `libname.a` | `name` (libc only) | libSystem |
| `osx-arm64` | Mach-O, ld64.lld | `libname.dylib` | `libname.a` | `name` (libc only) | libSystem |

x64 code is NASM (`.asm`). ARM64 code is GNU syntax (`.s`), which the bundled `llvm-mc`
assembles. The two can live side by side in one project.


## Choosing the RIDs

A project lists the RIDs its code supports, with .NET's property:

```xml
<PropertyGroup>
  <RuntimeIdentifiers>win-x64;linux-x64;linux-arm64;osx-arm64</RuntimeIdentifiers>
</PropertyGroup>
```

There is no default: a project that names no RID, and that nothing asks to build for a RID,
stops with **ASM0010**. A project gets its RIDs in one of three ways:

1. **Its own `RuntimeIdentifiers`** (or a single `RuntimeIdentifier`): `dotnet build` builds
   each one.
2. **Its referencing project.** An asm project that references a RID-less one builds it for
   its own RID, one inner build at a time. A .NET project can name the RIDs on the
   reference:
   ```xml
   <ProjectReference Include="../Crc32C/Crc32C.asmproj" RuntimeIdentifiers="linux-x64;win-x64" />
   ```
3. **The command line:** `dotnet build -r linux-arm64` builds exactly that RID. It must be one
   the project lists (**ASM0012**), and one AsmSdk builds (**ASM0011**).

A solution build is the one exception to ASM0010. A solution builds each project by itself,
with no RID, besides building it through its referencing projects. A project that lists no RID
on purpose prints "builds nothing for it by itself" there instead of failing.

### How the build runs

Each RID is an inner build with the standard `RuntimeIdentifier` global property, exactly
like `dotnet build -r`. So `$(RuntimeIdentifier)` conditions work in an `.asmproj` as in a
`.csproj`, and outputs land in `bin/<Configuration>/<TargetFramework>/<rid>/`:

```text
bin/Release/freestanding/win-x64/crc32c.dll
bin/Release/freestanding/linux-arm64/libcrc32c.so
bin/Release/freestanding/osx-arm64/libcrc32c.dylib
```

Without `RuntimeIdentifier`, the build is the *RID outer build*. It builds each RID in turn,
in parallel when the build is, and links nothing itself, just as the outer build of a
multi-targeted project (`TargetFrameworks`) builds each framework. `TargetFrameworks`
and `RuntimeIdentifiers` combine. A project may list different RIDs per framework:

```xml
<TargetFrameworks>freestanding;libc</TargetFrameworks>
<RuntimeIdentifiers>linux-x64;win-x64</RuntimeIdentifiers>
<RuntimeIdentifiers Condition="'$(TargetFramework)' == 'libc'">linux-x64</RuntimeIdentifiers>
```

`dotnet clean` cleans every RID; IDEs see each RID's output in their up-to-date checks.


## Sources per platform

Every `*.asm` and `*.s` file of the project is a source. Which of them builds a RID is decided
by the file name:

- **The extension is the language.** `.asm` is NASM, which assembles x86-64 only. `.s` is
  GNU syntax for `llvm-mc`, which assembles ARM64 (and x86-64 in AT&T syntax).
- **An unqualified file builds its CPU's RIDs.** `crc32c.asm` builds every x64 RID, and
  `crc32c.s` every ARM64 RID.
- **A qualifier before the extension narrows the targets.** It is a RID (`crc32c.win-x64.asm`),
  an OS family (`win`, `linux`, `linux-musl`, `osx`), `unix` (Linux and macOS), or a CPU
  (`x64`, `arm64`). `crc32c.linux.asm` builds every Linux x64 RID.
- **The closest file wins.** Files of one name in one folder compete per RID: a RID qualifier
  beats `linux-musl`, which beats an OS family, which beats `unix`, which beats a CPU, which
  beats no qualifier. A generic file can thus be overridden for one platform without
  excluding anything by hand:

| Files | win-x64 | linux-x64 | linux-musl-x64 | osx-x64 | ARM64 RIDs |
|---|---|---|---|---|---|
| `io.asm`, `io.win.asm`, `io.linux.asm`, `io.linux-musl.asm` | `io.win.asm` | `io.linux.asm` | `io.linux-musl.asm` | `io.asm` | (none) |
| `io.s`, `io.win-arm64.s` | (none) | (none) | (none) | (none) | `io.win-arm64.s` on win-arm64, `io.s` elsewhere |

Objects keep the qualifier (`io.linux.o`), so variants never share an object. The rules have
their own errors:

- **ASM0020:** two files of one name apply equally to a RID (`x.linux.asm` and `x.linux.s`).
- **ASM0021:** a `.asm` qualified for ARM64 only, which NASM cannot assemble.
- **ASM0023:** a listed RID that no source builds (typically ARM64 with only `.asm` files).
- **ASM0024:** one file listed twice as an `AsmSource` (the default glob plus an explicit
  item), as .NET reports `NETSDK1022` for `Compile` items.


## Writing code that runs everywhere

The three OSes agree on little below C: Windows and System V pass arguments in different
registers, and the three object formats name, export and address symbols differently. The SDK
puts a platform layer on every include path, and defines the platform's symbols.

### Defines

Every assembler run receives:

- `WINDOWS`, `LINUX` or `MACOS`, plus `MUSL` on `linux-musl-x64` (with `LINUX`);
- `X64` or `ARM64`;
- `FREESTANDING` or `LIBC`, and `DEBUG` or `RELEASE`, as before.

NASM gets them with `-D`, `llvm-mc` with `--defsym NAME=1`, so `%ifdef MACOS` in NASM and
`.ifdef MACOS` in GNU syntax test the same thing. `DisableImplicitFrameworkDefines=true`
turns off all but the configuration's, as in .NET.

### x64: asmsdk.inc

```nasm
default rel
%include "asmsdk.inc"

section .text
FUNCTION crc32c_append, 3, rbx      ; exported; 3 integer arguments; changes rbx
    mov     eax, ARG1d              ; ecx on Windows, edi elsewhere
    mov     rbx, ARG2               ; rdx / rsi
    ; ...
    RETURN                          ; restores rbx, then ret
ENDFUNCTION
```

| Name | Windows (Microsoft x64) | Linux, macOS (System V) |
|---|---|---|
| `ARG1` .. `ARG4` | `rcx`, `rdx`, `r8`, `r9` | `rdi`, `rsi`, `rdx`, `rcx` |
| `ARG5`, `ARG6` | `r10`, `r11`, loaded from the stack by `FUNCTION` when `nargs` says 5 or 6 | `r8`, `r9` |
| `ARG1d`, `ARG1w`, `ARG1b`, … | the 32-, 16- and 8-bit forms | |
| `SHADOW_SPACE` | 32 (a caller reserves it above the return address) | 0 |
| `PLT` | empty | `wrt ..plt` (calls into another module) |

- **`FUNCTION name [, nargs [, registers]]`** starts a global, exported function:
  `global name:function` for ELF, plain `global` for Mach-O (the SDK passes NASM
  `--gprefix _`, so every global and extern gets Mach-O's underscore), and on Windows also an
  `-export:name` directive in `.drectve`, which is what `__declspec(dllexport)` does. The
  registers listed are the ones the body changes. `FUNCTION` saves those the caller expects
  back *on this OS*: `rbx`, `rbp`, `r12`-`r15` everywhere, and `rdi`, `rsi`, `xmm6`-`xmm15`
  on Windows only. Listing `rsi` costs nothing on Linux and keeps Windows correct.
- **`LOCAL_FUNCTION`** is the same, not global. **`HIDDEN_FUNCTION`** is global but exported
  from no image: `hidden` on ELF, `private_extern` on Mach-O, no directive on Windows. Use it for
  what the library's other files, or a static library's users, call. **`RETURN`** restores and
  returns, and may appear more than once. **`ENDFUNCTION`** closes the function.
- **In a program, `FUNCTION` exports nothing.** The SDK defines `ASMSDK_EXECUTABLE` there, and
  `main` and `_start` are no exports (see [Programs export nothing by
  default](#the-c-library-per-platform)).
- **`section .rodata`** works on every OS: macho64 maps it to `__TEXT,__const`. On Windows the
  SDK passes `-D.rodata=.rdata`, because NASM's win64 output makes a section name it does not
  know *executable code*. In Debug builds (CodeView), NASM 3.02 also crashed on such a section.
- **`.note.GNU-stack` is Linux-only:** wrap it in `%ifdef LINUX`. For macho64 the line is an
  error (`unknown section name .note.GNU-stack`). win64 makes it one more empty, executable code
  section.

  ```nasm
  %ifdef LINUX
  section .note.GNU-stack noalloc noexec nowrite progbits
  %endif
  ```

  In a `.s` file, the same guard is `.ifdef LINUX` and `.section .note.GNU-stack,"",%progbits`.
  The SDK's own Linux links never need the note, because they pass `-z noexecstack`. It
  matters for a static library that someone else links with GNU `ld`.

Stack alignment is still yours: `rsp` is `16n+8` at entry, and each saved register moves it by
8. A Windows function that calls another reserves `SHADOW_SPACE` below its own data.
Floating-point arguments are positional on Windows (`xmm0`-`xmm3` together with the integer
ones), but sequential on System V. The macros name integer arguments only.

### ARM64: asmsdk-gnu.inc

AAPCS64 is the same calling convention on Linux, Windows and macOS: arguments in `x0`-`x7`,
the result in `x0`, with `x19`-`x28` and `d8`-`d15` callee-saved. So no argument macros are
needed. What differs is how symbols are named, declared, exported and addressed:

```asm
.include "asmsdk-gnu.inc"

RODATA                              // .rodata, .rdata or __TEXT,__const
table:  .word 10, 20, 30

FUNCTION lookup                     // global, exported, in .text, aligned
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ADDRESS x1, table               // adrp + :lo12:, or @PAGE / @PAGEOFF on Mach-O
    CALL    helper                  // bl helper, or bl _helper on Mach-O
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION lookup
```

| Macro | What it does |
|---|---|
| `FUNCTION name` | Global, exported function: `.type %function` and `.size` on ELF; `.def`/`.scl`/`.type 32` and the `-export:` directive on COFF; `_name` on Mach-O, plus the plain name as a local label so that `ADDRESS` takes one spelling. |
| `LOCAL_FUNCTION name` | The same, not global. |
| `HIDDEN_FUNCTION name` | The same, global but exported from no image: `.hidden` on ELF, `.private_extern` on Mach-O, no directive on COFF. |
| `ENDFUNCTION name` | Closes it (records the ELF size). |
| `CALL name` / `TAILCALL name` | `bl` / `b` with the OS's name for a C symbol: one of this module, of a linked static library, or of the C library (the linker routes it through a stub). On x86-64 GNU syntax: `call` / `jmp`, through `@PLT` on Linux. |
| `ADDRESS reg, label` | The position-independent address of a label of this file. |
| `RODATA` | Switches to read-only data, which each format names differently (`.section .rodata` is an error on Mach-O). |

`x18` is the platform register on Windows (the TEB) and macOS: never use it.
On macOS every *variadic* argument goes on the stack (Apple's ARM64 convention), while Windows
and Linux pass them in `x1`-`x7` like the others. `printf` from ARM64 assembly is therefore
written per OS.

### Exports

On Linux and macOS every global symbol of a shared library is exported. On Windows, only what
the DLL lists is: `FUNCTION` lists itself through its `.drectve` directive. An
**`ExportsFile`** (a linker version script) decides the exports on every OS:

- **Linux:** `--version-script`. The names are versioned (`crc32c_append@@CRC32C_1.0`), and
  `local: *` hides the rest.
- **Windows:** `-export:` for each global name. The SDK defines `ASMSDK_EXPORTS_FILE`, and
  `FUNCTION` then writes no directive of its own.
- **macOS:** `-exported_symbol _name` for each global name. Patterns are kept, and ld64.lld
  matches them itself.

A version script with `extern "C++"`, or a pattern on Windows, is **ASM0030**. One leak
remains on Windows: a referenced *static library's* `FUNCTION`s keep their directives and
are exported from the DLL that links them. lld-link cannot drop a directive. Keep a static
library's `FUNCTION`s to its public API. Use `HIDDEN_FUNCTION` for what its users call
internally, and `LOCAL_FUNCTION` for what one file uses alone. Linux and macOS export a
static library's globals from every shared library that links it too, unless an
`ExportsFile` or `HIDDEN_FUNCTION` hides them.

Linux shared libraries are linked `-Bsymbolic`, so a library's references to its own global
symbols bind to the library, as on Windows and macOS. Without it, `lea [rel global]` or an
`adrp` to a global symbol failed to link ("recompile with -fPIC"). A library that wants ELF's
interposition back adds `<LinkerArg Include="-Bno-symbolic" />`.


## The C library per platform

`<TargetFramework>libc</TargetFramework>` links the platform's C library, with an ordinary
`int main(int argc, char **argv)` for a program. The SDK links against stand-ins it writes
itself from its symbol lists, so no build machine needs the target's sysroot or SDK:

| Platform | Linked against | A program starts in | Notes |
|---|---|---|---|
| Linux glibc (x64, ARM64) | a stub `libc.so.6` | the SDK's `crt1` → `__libc_start_main` | glibc 2.34+; loader `/lib64/ld-linux-x86-64.so.2` or `/lib/ld-linux-aarch64.so.1` |
| Linux musl (x64) | a stub `libc.musl-x86_64.so.1` | the same `crt1` (musl's `__libc_start_main` takes the same arguments) | loader `/lib/ld-musl-x86_64.so.1` |
| macOS | `libSystem.tbd` (install name `/usr/lib/libSystem.B.dylib`) | `main`, called by dyld (`LC_MAIN`) | macOS 11+ (`AsmMacOSMinimumVersion`) |
| Windows | `ucrtbase.lib` (the Universal C Runtime, part of Windows 10+) | the SDK's `mainCRTStartup` | `printf`, `fprintf`, `sprintf`, `snprintf`, their `v` forms, `sscanf` and `atexit` come from the SDK's wrappers |

What a C compiler would take from static runtime archives, the SDK provides itself:

- `atexit` on glibc, which keeps it in `libc_nonshared.a`. In 0.2.0 a program that called it
  linked but did not start.
- The `printf` family on Windows. The UCRT headers define those functions inline, over
  `__stdio_common_vfprintf`, so `ucrtbase.dll` exports none of them.

A function missing from the SDK's list fails the link with "undefined symbol". Add it with
`<AsmLibcSymbol Include="name" />` (a C name; macOS gets the underscore). The lists, and what
the SDK writes from them, are in
[Runtimes and linking](runtimes-and-linking.md#the-c-library-stand-ins).

**kernel32 on every Windows output.** Windows system call numbers change between releases, so
kernel32 is a Windows program's way to the OS. A freestanding Windows program may call
`GetStdHandle`, `WriteFile` and `ExitProcess`. The import library adds an import only for the
names a program uses. [Freestanding programs](runtimes-and-linking.md#freestanding-programs)
has a complete one.

**Programs export nothing by default.** `FUNCTION` writes no export directive in an executable
(the SDK defines `ASMSDK_EXECUTABLE` there), so `main` and `_start` are no exports. The `.exe`
gets no import library either. A program that exports, such as a plugin host, names its
exports in an `ExportsFile`. On Windows it then also gets `<name>.lib`, for its plugins to
link against.

**macOS has no freestanding programs:** Apple supports no static executables, and dyld starts
every program, with libSystem (**ASM0015**). Freestanding *libraries* are fine.

Some differences remain the code's to handle:

- errno is `__error()` on macOS, `__errno_location()` on glibc, `_errno()` on Windows.
- `stdout` is the data object `stdout` on Linux, `__stdoutp` on macOS, and
  `__acrt_iob_func(1)` on Windows.
- On osx-x64, `stat` and friends are the old 32-bit-inode versions unless the code calls
  `stat$INODE64`.


## Build machines

The bundled toolchain (`AsmFoundation.Toolchain.<host>`) exists for the machines LLVM publishes
release binaries for:

| Build machine | Toolchain package | Notes |
|---|---|---|
| Windows x64 | `AsmFoundation.Toolchain.win-x64` | |
| Windows on Arm | `AsmFoundation.Toolchain.win-arm64` | native LLVM tools; NASM's x64 build runs under Windows' x64 emulation |
| Linux x64 (glibc) | `AsmFoundation.Toolchain.linux-x64` | |
| Linux ARM64 (glibc) | `AsmFoundation.Toolchain.linux-arm64` | |
| Apple silicon Mac | `AsmFoundation.Toolchain.osx-arm64` | |
| Intel Mac, Alpine | none (**ASM0006**) | `<AsmToolchain>System</AsmToolchain>` with Homebrew's or Alpine's `nasm`, `llvm` and `lld`; or `AsmHostRid` to a toolchain that runs there |

Every toolchain builds every RID. `llvm-mc` arrived in toolchain 0.2.0; with an older one, a
`.s` source stops with **ASM0004**. The System toolchain links with GNU `ld` by default,
which links ELF of the build machine's own architecture only. Windows and macOS RIDs need
`AsmLinkerCommand` set to `lld-link` or `ld64.lld`, and the Linux RID of the other
architecture `ld.lld` (**ASM0005**).
