# Writing NASM for AsmSdk

NASM 3.02 builds the four x64 RIDs: `win-x64`, `linux-x64`, `linux-musl-x64` and `osx-x64`.
ARM64 code is GNU syntax for `llvm-mc` (see [Platforms](platforms.md)). This page covers what a
NASM source needs in order to work inside an AsmSdk project, on each OS: position-independent
code, symbols, sections, includes, the calling conventions and Linux system calls. Each error
quoted here was reproduced with the bundled NASM 3.02 and lld 23.1.5. It is not a general NASM
tutorial.


## A source file skeleton

```nasm
; crc.asm - one public function; see include/crc.inc for its contract.
default rel                         ; RIP-relative addressing for every [label] operand
bits 64

%include "asmsdk.inc"               ; FUNCTION, ARG1.., SHADOW_SPACE, PLT, per OS
%include "crc.inc"                  ; public constants and extern declarations

section .text

; uint32_t crc_update(uint32_t crc, const uint8_t *data, size_t len)
FUNCTION crc_update, 3
    mov     eax, ARG1d
    ; ...
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```

**Why each line is there:**

- **`default rel`** is required on every OS, and harmless in a Linux freestanding program.
  Without it, a memory operand is a 32-bit absolute address. NASM 3 warns
  `implicit DEFAULT ABS is deprecated`, and then:
  - Linux: the link of a shared library or a PIE fails (see below);
  - macOS: NASM stops: `Mach-O 64-bit format does not support 32-bit absolute addresses`;
  - Windows: lld-link links it into a DLL that loads above 4 GB, where the address is wrong,
    and the SDK stops the build with **ASM0080**.
- **`bits 64`** is implied by every 64-bit output format, but it states the intent.
- **`asmsdk.inc`** gives the OS's calling convention and export rules names (see
  [Platforms](platforms.md#x64-asmsdkinc)): `FUNCTION` declares and exports, `ARG1d` is `ecx`
  on Windows and `edi` elsewhere.
- **`.note.GNU-stack`** marks the object as not needing an executable stack. It is
  Linux-only: for macOS, NASM stops at the line (`unknown section name`). The SDK already
  links with `-z noexecstack`, but your `.a` may be linked by someone else's toolchain. Since
  binutils 2.39, GNU `ld` warns about a missing note, and it can make the stack executable.

A file for one OS (`crc.linux.asm`) can use that OS's directives directly, as the tables below
show. `asmsdk.inc` is what lets one file serve all four x64 RIDs.

## Position-independent code

Every output is position-independent, except a Linux freestanding program. A shared library
or a libc program on Linux is PIC (a PIE), every Windows image can be moved by ASLR, and macOS
has nothing else. RIP-relative addressing is as fast as absolute addressing on x86-64, so
writing it everywhere costs nothing, and the same object then goes into any kind of output.

| Write | Not | What the wrong form gives |
|---|---|---|
| `default rel` + `lea rsi, [msg]` | `lea rsi, [msg]` without `default rel` | NASM warns: `implicit DEFAULT ABS is deprecated` and `32-bit absolute section-crossing relocation`. Linux `.so`/PIE: `relocation R_X86_64_32S cannot be used against local symbol; recompile with -fPIC`. macOS: NASM error `Mach-O 64-bit format does not support 32-bit absolute addresses`. Windows: **ASM0080** (lld-link alone would accept it) |
| `lea rax, [msg]` | `mov rax, msg` | Linux `.so`/PIE: `relocation R_X86_64_64 cannot be used against local symbol; recompile with -fPIC`. macOS: **ASM0081**, a text relocation. Windows: works, at the cost of a load-time fixup |
| `call memcpy PLT` (a function of another library) | `call memcpy` in a Linux `.so` | `relocation R_X86_64_PC32 cannot be used against symbol 'memcpy'; recompile with -fPIC`. A PIE accepts it. `PLT` is empty on Windows and macOS, whose linkers add the stub themselves |
| `mov rax, [rel stdout wrt ..got]` then `mov rdi, [rax]` | `mov rdi, [rel stdout]` (data of another library) in a Linux `.so` | `relocation R_X86_64_PC32 cannot be used against symbol 'stdout'`. A PIE accepts it, through a copy relocation. On macOS the GOT is `wrt ..gotpcrel`; `wrt ..got` and `wrt ..plt` are ELF-only, and NASM stops on them elsewhere (`symbol '..got' not defined`) |
| jump table of offsets in `.rodata`: `dd .case1 - table` | `dq .case1` in `.rodata` | Linux `.so`/PIE: `relocation R_X86_64_64 cannot be used against local symbol` (a freestanding program, linked `-static`, accepts it). macOS: **ASM0081**, since `.rodata` is in the read-only `__TEXT` segment there |

**Calls and data inside one library.** The SDK links Linux shared libraries `-Bsymbolic`. A
`global` function or data symbol of the library therefore binds to the library itself, as on
Windows and macOS. A plain `call helper` or `[rel table]` to another object of the same library
links, and is direct. Measured: with `-Bno-symbolic`, both fail with `recompile with -fPIC`.
The PLT and the GOT are only for the symbols of *another* library: the C library, or a
referenced shared library.

- **Use `PLT` only for other libraries.** NASM 3.02 stops with
  `panic: intra-segment OUT_REL4ADR` on `call helper wrt ..plt` when `helper` is defined in
  the same section of the same file.
- **What the library exports still matters.** A `global` helper of one file, called from
  another, is exported on Linux and macOS like the API. Declare it with `HIDDEN_FUNCTION`
  (global within the library, exported from no OS), or list the API in an
  [`ExportsFile`](platforms.md#exports). For a helper of one file, use `LOCAL_FUNCTION`.

**Jump tables.** In a shared library, a PIE or any macOS or Windows image, an absolute address
in data needs a load-time fixup. That fixup is only allowed in a writable section: `.data`, or
on Linux `section .data.rel.ro progbits alloc noexec write align=8`. That section becomes
read-only after relocation (RELRO), but **only when it is declared `write`**: NASM does not
know `.data.rel.ro`, makes it read-only, and the link fails. A table of 32-bit offsets needs no
fixup at all, and works in `.rodata` everywhere:

```nasm
section .rodata                     ; read-only, no fixups: works on every OS
align 4
table:  dd .case0 - table, .case1 - table
section .text
    lea     rdx, [table]
    movsxd  rax, dword [rdx + rcx*4]
    add     rax, rdx
    jmp     rax
```

`dq .case0` in `.data` works on every OS (`R_X86_64_RELATIVE` on Linux, a base relocation on
Windows, a rebase on macOS).

## Symbols: export, hide, size

`asmsdk.inc` writes the right directive for each format:

| Macro | Windows (win64) | Linux (elf64) | macOS (macho64) |
|---|---|---|---|
| `FUNCTION f` | `global f`, plus `-export:f` in `.drectve` | `global f:function` | `global f` (`_f`) |
| `HIDDEN_FUNCTION f` | `global f`, no directive | `global f:function hidden` | `global f:private_extern` |
| `LOCAL_FUNCTION f` | a label | a label | a label |

The directives differ because the formats do. Measured with NASM 3.02:

- `global f:function` is ELF's: win64 accepts and ignores it, and macho64 stops
  (`unrecognised symbol type 'function'`).
- `hidden` is ELF's, and `private_extern` is Mach-O's. Each format stops on the other's.
- A Windows DLL exports only what it is told to: a `.drectve` directive, or the
  `ExportsFile`. A plain `global` is not exported. On Linux and macOS, every `global` is
  exported unless it is hidden, or an `ExportsFile` says otherwise.

On ELF (Linux-only files), the directives also give sizes and data types:

| Directive | Result in a `.so` |
|---|---|
| `global f:function` | exported, `Type: Function`, `Size: 0` |
| `global f:function (f.end - f)` with a local `.end:` label | exported, `Type: Function`, correct size |
| `global g` | exported, `Type: None`, `Size: 0` |
| `global h:function hidden` | not exported; callable from other objects of the same library without the PLT |
| `global h:function hidden (h.end - h)` | hidden, with a size: **the visibility comes before the size** |
| `global h:function (h.end - h) hidden` | **exported and preemptible**: NASM ignores a visibility written after the size, without any warning |
| `global v:data 8` | exported data object of 8 bytes |
| `global v:data hidden 8` | hidden 8-byte data object (`global v:data 8 hidden` is silently **not** hidden) |
| `extern f` | an import; with `-z defs` it must be defined somewhere at link time |
| `common buf 4096:64` | a common symbol: avoid in libraries, use `.bss` |

- **Symbol names that are NASM keywords or instruction mnemonics** need a `$` prefix in
  definitions and references: `$wait`, `$syscall`, `$rel`. Without it, `rel:` fails with
  `label, instruction or prefix expected at start of line, found 'rel'`.
- **A static library's exports are exported again** by every shared library that links it:
  its global symbols on Linux and macOS, its `FUNCTION`s' directives on Windows. Declare a
  static library's internals with `HIDDEN_FUNCTION` or `LOCAL_FUNCTION`. On Linux,
  `--exclude-libs ALL` in the `.so` also works (see
  [Runtimes and linking](runtimes-and-linking.md#production-link-options)).

## Sections and data

| Section | Use | Notes |
|---|---|---|
| `.text` | code | read-only and executable |
| `.rodata` | constants, string literals, offset tables | read-only. No absolute addresses in it, except in a Linux freestanding program. The SDK maps the name to `.rdata` for Windows, and macho64 to `__TEXT,__const` |
| `.data` | initialized writable data | absolute addresses (`dq label`) are fine here, on every OS |
| `.bss` | zeroed writable data | reserve with `resb`, `resq`; align with `alignb 64` (`align` pads with `nop` bytes and is meant for code) |
| `.data.rel.ro` | pointers that are fixed after load (Linux) | must be declared `progbits alloc noexec write`. An ELF name: Linux-only files |
| `.text.<name>` | one function per section | only useful with `--gc-sections`. An ELF name: Linux-only files |

**Alignment:**

- In code, use `align 16` (NASM pads with multi-byte `nop`s).
- In `.bss`, use `alignb 64` (cache line).
- Data can only be as aligned as its section: in elf64, NASM gives `.data` 4-byte and `.text`
  16-byte alignment unless you declare more, with `section .data align=64`.

## Includes and macros

- **How `%include "x.inc"` resolves**:
  1. it tries the name relative to the **working directory**, which is the project folder;
  2. then each `-I` folder in order: the project's `include/` and the project folder, then
     the include folders of referenced projects and packages, then the SDK's own `include/`
     (`asmsdk.inc`, `asmsdk-gnu.inc`).

  NASM does **not** look in the folder of the file that holds the `%include`. A source in
  `src/x86/a.asm` including `"b.inc"` from the same folder fails with
  `unable to open include file 'b.inc'`. Write `%include "src/x86/b.inc"`, or add
  `<AsmIncludeDirectory Include="src/x86/" />`.
- **Shadowing:** a file in the project folder wins over a same-named file in the project's
  `include/`, and both win over packages and the SDK. Give public include files a prefix
  (`crc_types.inc`, not `types.inc`).
- **Guard every include file:**

  ```nasm
  %ifndef ACME_CRC_INC
  %define ACME_CRC_INC
  ; constants, struc definitions, macros, extern declarations
  %endif
  ```

- **Only `.inc` files are include files.** An `.asm` file is a source: if another file
  `%include`s it, its symbols are assembled twice, giving `duplicate symbol`.
- **Defines from the project:** `<DefineConstants>$(DefineConstants);LEVEL=3</DefineConstants>`
  becomes `-DLEVEL=3`. Test it with `%ifdef LEVEL` / `%if LEVEL >= 2`. The SDK also defines:
  - `DEBUG` or `RELEASE`, and `FREESTANDING` or `LIBC`;
  - `WINDOWS`, `LINUX` or `MACOS`, plus `MUSL` on `linux-musl-x64`, and `X64`;
  - `ASMSDK_EXPORTS_FILE` in a project with an `ExportsFile`, and `ASMSDK_EXECUTABLE` in a
    program.

  A value with spaces or quotes is not quoted for the shell, so keep values simple: numbers
  and identifiers.
- **Structures:** `struc point` / `.x: resd 1` / `.y: resd 1` / `endstruc`, then
  `mov eax, [rdi + point.y]`. `point_size` holds the size. Put the shared `struc`
  definitions in `include/`, so callers in other projects and packages use the same
  offsets.

## Calling conventions

Linux and macOS follow the System V AMD64 ABI. Windows follows the Microsoft x64 convention.
Every caller on the OS uses it: C, .NET P/Invoke, Python `ctypes`. `asmsdk.inc` names the
integer argument registers (`ARG1`..`ARG6`) and saves the callee-saved ones `FUNCTION` lists,
for the OS being built.

| | System V (Linux, macOS) | Microsoft x64 (Windows) |
|---|---|---|
| Integer and pointer arguments | `rdi`, `rsi`, `rdx`, `rcx`, `r8`, `r9`, then the stack | `rcx`, `rdx`, `r8`, `r9`, then the stack |
| Floating-point arguments | `xmm0`–`xmm7`, counted apart from the integer ones | `xmm0`–`xmm3`, by position: the 2nd argument is `rdx` or `xmm1` |
| Return value | `rax` (and `rdx` for 128-bit); `xmm0` (and `xmm1`) | `rax`; `xmm0` |
| Preserved across calls (callee-saved) | `rbx`, `rbp`, `r12`–`r15`, `rsp` | `rbx`, `rbp`, `rdi`, `rsi`, `r12`–`r15`, `rsp`, `xmm6`–`xmm15` |
| Free to clobber (caller-saved) | `rax`, `rcx`, `rdx`, `rsi`, `rdi`, `r8`–`r11`, `xmm0`–`xmm15`, flags | `rax`, `rcx`, `rdx`, `r8`–`r11`, `xmm0`–`xmm5`, flags |
| Space the caller reserves | none | 32 bytes above the return address (`SHADOW_SPACE`), for the callee |
| Below `rsp` | a 128-byte red zone a leaf function may use | nothing: anything below `rsp` may be overwritten |

**Stack and call rules:**

- **Alignment:** `rsp` is 16-byte aligned at each `call` instruction, on both. A function is
  entered with `rsp ≡ 8 (mod 16)`, so before calling anything it adjusts by `8 + 16k` bytes
  (one push does it). On Windows it also reserves `SHADOW_SPACE` (32 bytes) for its callee:
  `sub rsp, SHADOW_SPACE + 8` covers both on either OS.
- **Variadic calls (`printf`):** on System V, `al` = an upper bound on the number of vector
  registers used. On Windows, a floating-point variadic argument also goes in the matching
  integer register.
- **Direction flag:** clear (`DF = 0`) on entry and on return, on both.
- **32-bit results:** writing `eax` zeroes the upper half of `rax`, but callers may ignore
  bits 32–63 of an `int` argument. Do not assume they are zero; use `mov eax, ARG1d` or
  `movsxd rax, ARG1d`.
- **AVX:** after using `ymm` or `zmm` registers, `vzeroupper` before returning to code that
  may use SSE. This avoids transition penalties on many CPUs.
- **Windows unwind information:** Windows unwinds a stack (exceptions, debuggers, crash
  dumps) through `.pdata` and `.xdata` tables, which NASM writes only by hand. Leaf functions
  that do not move `rsp` need none.

## Linux system calls

Freestanding Linux code talks to the kernel directly:

- `syscall` with the number in `rax` and arguments in `rdi`, `rsi`, `rdx`, `r10` (not
  `rcx`), `r8`, `r9`.
- The kernel clobbers `rcx` and `r11`.
- The result comes back in `rax`. Values from `-4095` to `-1` are `-errno`.

| Call | Number | Call | Number |
|---|---|---|---|
| `read` | 0 | `mmap` | 9 |
| `write` | 1 | `munmap` | 11 |
| `openat` | 257 | `clock_gettime` | 228 |
| `close` | 3 | `getrandom` | 318 |
| `exit_group` | 231 | `futex` | 202 |

Prefer `exit_group` to `exit` (number 60), which ends only the calling thread. These numbers
are x64's: ARM64 Linux uses the generic table (`write` is 64, `exit_group` 94, through
`svc #0` with the number in `x8`). Windows and macOS change their system call numbers between
releases: there, a program goes through kernel32 or libSystem (see
[Freestanding programs](runtimes-and-linking.md#freestanding-programs)).

**Example:** [HexDump](../examples/hexdump.md) is a complete program in system calls alone: `EINTR`, short writes, buffered output and `errno` messages included.

## Warnings worth knowing

The SDK assembles with `-w+all` except `reloc-rel-dword` and `reloc-abs-qword`. These are
the ones a real codebase meets:

| Warning (NASM 3.02 text) | Meaning |
|---|---|
| `implicit DEFAULT ABS is deprecated [-w+implicit-abs-deprecated]` | a memory operand in a file without `default rel` or `default abs` |
| `32-bit absolute section-crossing relocation [-w+reloc-abs-dword]` | an absolute 32-bit address: wrong in every output except a Linux freestanding program. On Windows, the image fails with ASM0080. NASM also gives it for `dd label wrt ..imagebase`, the image-relative form Windows unwind tables use, which is right |
| `label 'x' alone on a line without a colon might be in error [-w+label-orphan]` | a typo, or a label without `:` |
| `dword exceeds bounds [-w+number-overflow]` | a constant does not fit its size |

- **Silence one warning** with `<AsmFlags>-w-label-orphan</AsmFlags>`, or for one region
  with `[warning push]`, `[warning -label-orphan]` … `[warning pop]`.
- **Make them count:** add `-Xvc` to `AsmFlags`, and every warning becomes an MSBuild
  warning with file and line. `TreatWarningsAsErrors` makes them errors, as in a `.csproj`.

## Templates

Both templates build for the four x64 RIDs from one file each.

**A function for a shared library that calls a helper from another file and a C library
function** (`TargetFramework=libc`):

```nasm
; copy_and_sum.asm
default rel
%include "asmsdk.inc"
extern helper_checksum              ; uint32_t helper_checksum(const void *p, size_t n), helper.asm
extern memcpy

section .text
; void copy_and_sum(void *dst, const void *src, size_t n, uint32_t *sum)
FUNCTION copy_and_sum, 4, rbx, r12
    sub     rsp, SHADOW_SPACE + 8   ; entered at 16k+8; two pushes + 8 = 16-byte aligned calls
    mov     rbx, ARG4               ; sum: callee-saved, so it survives the calls
    mov     r12, ARG3               ; n
    call    memcpy PLT              ; ARG1..ARG3 already hold dst, src, n; returns dst
    mov     ARG1, rax
    mov     ARG2, r12
    call    helper_checksum         ; the same library: a direct call
    mov     [rbx], eax
    add     rsp, SHADOW_SPACE + 8
    RETURN                          ; restores r12 and rbx
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```

`helper.asm`, in the same project:

```nasm
default rel
%include "asmsdk.inc"

section .text
; uint32_t helper_checksum(const void *p, size_t n): a byte sum, as a placeholder for a real one.
HIDDEN_FUNCTION helper_checksum, 2  ; called by copy_and_sum.asm, exported by no OS
    xor     eax, eax
    test    ARG2, ARG2
    jz      .done
.loop:
    movzx   r8d, byte [ARG1]        ; r8: volatile on both conventions, and no argument here
    add     eax, r8d
    inc     ARG1
    dec     ARG2
    jnz     .loop
.done:
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```

**A function exported to .NET or Python without a C library** (`freestanding`):

```nasm
default rel
%include "asmsdk.inc"

section .text
; int64_t sum_i32(const int32_t *values, int64_t count)
FUNCTION sum_i32, 2
    xor     eax, eax
    test    ARG2, ARG2
    jle     .done
.loop:
    movsxd  r8, dword [ARG1]
    add     rax, r8
    add     ARG1, 4
    dec     ARG2
    jnz     .loop
.done:
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```
