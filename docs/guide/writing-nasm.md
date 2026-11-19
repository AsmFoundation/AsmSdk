# Writing NASM for AsmSdk

This page covers what a NASM 3.02 source needs in order to work inside an AsmSdk project.
That means position-independent code, symbols, sections, includes, the System V ABI and
Linux system calls. Each linker error quoted here was reproduced with the bundled
`ld.lld` 23.1.5. It is not a general NASM tutorial.


## A source file skeleton

```nasm
; crc.asm - one public function; see include/crc.inc for its contract.
default rel                         ; RIP-relative addressing for every [label] operand
bits 64

%include "crc.inc"                  ; public constants and extern declarations

section .text

; uint32_t crc_update(uint32_t crc, const uint8_t *data, size_t len)
global crc_update:function (crc_update.end - crc_update)
crc_update:
    mov     eax, edi
    ; ...
    ret
.end:

section .note.GNU-stack noalloc noexec nowrite progbits
```

**Why each line is there:**

- **`default rel`** is required for any shared library or libc executable, and harmless
  elsewhere. Without it NASM 3 warns `implicit DEFAULT ABS is deprecated`, and the link
  fails (see below).
- **`bits 64`** is implied by `-f elf64`, but it states the intent.
- **`global name:function (size)`** gives the symbol its ELF type and size.
- **`.note.GNU-stack`** marks the object as not needing an executable stack. The SDK
  already links with `-z noexecstack`, but your `.a` may be linked by someone else's
  toolchain. Since binutils 2.39, GNU `ld` warns about a missing note, and it can make the
  stack executable.

## Position-independent code

A shared library is always position-independent, and so is a libc executable (a PIE). A
freestanding executable is not, but writing PIC everywhere costs nothing on x86-64
(RIP-relative addressing is as fast as absolute addressing). It also lets the same object
go into either kind of output.

| Write | Not | What the wrong form gives |
|---|---|---|
| `default rel` + `lea rsi, [msg]` | `lea rsi, [msg]` without `default rel` | NASM: `implicit DEFAULT ABS is deprecated` and `32-bit absolute section-crossing relocation`; link of a `.so`/PIE: `relocation R_X86_64_32S cannot be used against local symbol; recompile with -fPIC` |
| `lea rax, [msg]` | `mov rax, msg` | `.so`/PIE: `relocation R_X86_64_64 cannot be used against local symbol; recompile with -fPIC` (an absolute address in read-only `.text`) |
| `call helper wrt ..plt` (helper in another object or library) | `call helper` | `.so`: `relocation R_X86_64_PC32 cannot be used against symbol 'helper'; recompile with -fPIC` |
| `mov rax, [rel stdout wrt ..got]` then `mov rdi, [rax]` | `mov rdi, [rel stdout]` | `.so`: `relocation R_X86_64_PC32 cannot be used against symbol 'stdout'`. This also applies to a `global` data symbol defined in **another object of the same library**, unless that symbol is `hidden`. A PIE accepts it, through a copy relocation |
| jump table of offsets in `.rodata`: `dd .case1 - table` | `dq .case1` in `.rodata` | `.so`/PIE: `relocation R_X86_64_64 cannot be used against local symbol` (a freestanding executable, linked `-static`, accepts it) |

**Calls inside one library.** A `global` function is "preemptible" in a shared library:
another library loaded earlier may define the same name, and the dynamic loader would
pick that one. So a call to it from another object must go through the PLT. There are
three ways to write it:

1. **`call helper wrt ..plt`.** It works everywhere, and costs one indirect jump per call
   through the PLT in a `.so`. Linked into an executable, the linker turns it into a
   direct call. One exception: when `helper` is defined in the same section of the same
   file, NASM 3.02 stops with `panic: intra-segment OUT_REL4ADR`. Use a plain `call helper`
   there (option 3).
2. **Make the helper hidden in its defining file:** `global helper:function hidden`. A
   plain `call helper` then links. The call is direct, and `helper` is no
   longer exported, which is right for internal helpers.
3. **Define the helper in the same source and section as its caller.** NASM resolves the
   call itself, with no relocation, and interposition is bypassed.

For a hot internal call path, use option 2. Export only the API.

**Jump tables.** In a shared library or a libc executable (a PIE), an absolute address in
data needs a dynamic relocation, and that relocation is only allowed in a writable section.
A freestanding executable is linked `-static`: its addresses are fixed at link time, so
`dq .case0` works there in any section.

```nasm
section .rodata                     ; read-only, no relocations: preferred
align 4
table:  dd .case0 - table, .case1 - table
section .text
    lea     rdx, [table]
    movsxd  rax, dword [rdx + rcx*4]
    add     rax, rdx
    jmp     rax
```

- `dq .case0` works in `.data` (it becomes `R_X86_64_RELATIVE`;).
- It also works in `section .data.rel.ro progbits alloc noexec write align=8`. That section
  becomes read-only after relocation (RELRO), but **only when it is declared `write`**:
  NASM does not know `.data.rel.ro`, makes it read-only, and the link fails.

## Symbols: export, hide, size

| Directive | Result in a `.so` |
|---|---|
| `global f:function` | exported, `Type: Function`, `Size: 0` |
| `global f:function (f.end - f)` with a local `.end:` label | exported, `Type: Function`, correct size |
| `global g` | exported, `Type: None`, `Size: 0` |
| `global h:function hidden` | not exported; callable from other objects of the same library without the PLT |
| `global h:function hidden (h.end - h)` | hidden, with a size: **the visibility comes before the size** |
| `global h:function (h.end - h) hidden` | **exported and preemptible**: NASM ignores a visibility written after the size, without any warning |
| `global v:data 8` | exported data object of 8 bytes |
| `global v:data hidden 8` | hidden 8-byte data object (`global v:data 8 hidden` is silently **not** hidden;) |
| `extern f` | an import; with `-z defs` it must be defined somewhere at link time |
| `common buf 4096:64` | a common symbol: avoid in libraries, use `.bss` |

- **Symbol names that are NASM keywords or instruction mnemonics** need a `$` prefix in
  definitions and references: `$wait`, `$syscall`, `$rel`. Without it, `rel:` fails with
  `label, instruction or prefix expected at start of line, found 'rel'`.
- **A static library's globals are exported again** by any `.so` that links it. Use
  `hidden` in the library, or `--exclude-libs ALL` in the `.so` (see
  [Runtimes and linking](runtimes-and-linking.md)).

## Sections and data

| Section | Use | Notes |
|---|---|---|
| `.text` | code | read-only and executable |
| `.rodata` | constants, string literals, offset tables | read-only; no absolute addresses in a `.so` |
| `.data` | initialized writable data | absolute addresses (`dq label`) are fine here |
| `.bss` | zeroed writable data | reserve with `resb`, `resq`; align with `alignb 64` (`align` pads with `nop` bytes and is meant for code) |
| `.data.rel.ro` | pointers that are fixed after load | must be declared `progbits alloc noexec write` |
| `.text.<name>` | one function per section | only useful with `--gc-sections` |

**Alignment:**

- In code, use `align 16` (NASM pads with multi-byte `nop`s).
- In `.bss`, use `alignb 64` (cache line).
- Data can only be as aligned as its section: NASM gives `.data` 4-byte and `.text`
  16-byte alignment unless you declare more, with `section .data align=64`.

## Includes and macros

- **How `%include "x.inc"` resolves**:
  1. it tries the name relative to the **working directory**, which is the project folder;
  2. then each `-I` folder in order: the project's `include/`, the project folder, then
     include folders from referenced projects and packages.

  NASM does **not** look in the folder of the file that holds the `%include`. A source in
  `src/x86/a.asm` including `"b.inc"` from the same folder fails with
  `unable to open include file 'b.inc'`. Write `%include "src/x86/b.inc"`, or add
  `<AsmIncludeDirectory Include="src/x86/" />`.
- **Shadowing:** a file in the project folder wins over a same-named file in the project's
  `include/`, and both win over packages. Give public include files a prefix
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
  becomes `-DLEVEL=3`. Test it with `%ifdef LEVEL` / `%if LEVEL >= 2`. The SDK also defines
  `DEBUG` or `RELEASE`, and `FREESTANDING` or `LIBC`, so one source can serve both target
  frameworks: `%ifdef LIBC`. A value with spaces or quotes is not quoted for
  the shell, so keep values simple: numbers and identifiers.
- **Structures:** `struc point` / `.x: resd 1` / `.y: resd 1` / `endstruc`, then
  `mov eax, [rdi + point.y]`. `point_size` holds the size. Put the shared `struc`
  definitions in `include/`, so callers in other projects and packages use the same
  offsets.

## The System V AMD64 ABI

This is the calling convention of every Linux x86-64 caller: C, .NET P/Invoke on Linux,
Python `ctypes`.

| | Registers |
|---|---|
| Integer and pointer arguments 1–6 | `rdi`, `rsi`, `rdx`, `rcx`, `r8`, `r9` (then the stack, right to left) |
| Floating-point arguments 1–8 | `xmm0`–`xmm7` |
| Return value | `rax` (and `rdx` for 128-bit); `xmm0` (and `xmm1`) for floating point |
| Preserved across calls (callee-saved) | `rbx`, `rbp`, `r12`, `r13`, `r14`, `r15`, `rsp`; the x87 control word and the `MXCSR` control bits |
| Free to clobber (caller-saved) | `rax`, `rcx`, `rdx`, `rsi`, `rdi`, `r8`–`r11`, `xmm0`–`xmm15`, flags |

**Stack and call rules:**

- **Alignment:** `rsp` is 16-byte aligned at each `call` instruction. A function is entered
  with `rsp ≡ 8 (mod 16)`, so before calling anything it adjusts by `8 + 16k` bytes (one
  push does it).
- **Red zone:** a leaf function may use the 128 bytes below `rsp` without moving it.
  Signal handlers respect the red zone.
- **Variadic calls (`printf`):** `al` = an upper bound on the number of vector registers
  used.
- **Direction flag:** clear (`DF = 0`) on entry and on return.
- **32-bit results:** writing `eax` zeroes the upper half of `rax`, but callers may ignore
  bits 32–63 of an `int` argument. Do not assume they are zero; use `mov eax, edi` or
  `movsxd rax, edi`.
- **AVX:** after using `ymm` or `zmm` registers, `vzeroupper` before returning to code that
  may use SSE. This avoids transition penalties on many CPUs.

## Linux system calls

Freestanding code talks to the kernel directly:

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

Prefer `exit_group` to `exit` (number 60), which ends only the calling thread.

**Example:** [HexDump](../examples/hexdump.md) is a complete program in system calls alone: `EINTR`, short writes, buffered output and `errno` messages included.

## Warnings worth knowing

The SDK assembles with `-w+all` except `reloc-rel-dword` and `reloc-abs-qword`. These are
the ones a real codebase meets:

| Warning (NASM 3.02 text) | Meaning |
|---|---|
| `implicit DEFAULT ABS is deprecated [-w+implicit-abs-deprecated]` | a memory operand in a file without `default rel` or `default abs` |
| `32-bit absolute section-crossing relocation [-w+reloc-abs-dword]` | an absolute 32-bit address: wrong in any PIC output |
| `label 'x' alone on a line without a colon might be in error [-w+label-orphan]` | a typo, or a label without `:` |
| `dword exceeds bounds [-w+number-overflow]` | a constant does not fit its size |

- **Silence one warning** with `<AsmFlags>-w-label-orphan</AsmFlags>`, or for one region
  with `[warning push]`, `[warning -label-orphan]` … `[warning pop]`.
- **Make them count:** add `-Xvc` to `AsmFlags`, and every warning becomes an MSBuild
  warning with file and line.

## Templates

**A function for a shared library that calls a helper from another file and a libc
function** (`TargetFramework=libc`):

```nasm
default rel
extern helper_checksum              ; uint32_t helper_checksum(const void *p, size_t n),
                                    ; defined "global helper_checksum:function hidden" in helper.asm
extern memcpy
section .text
; void copy_and_sum(void *dst, const void *src, size_t n, uint32_t *sum)
global copy_and_sum:function (copy_and_sum.end - copy_and_sum)
copy_and_sum:
    push    rbx                     ; callee-saved: will hold sum
    push    r12                     ; callee-saved: will hold n
    sub     rsp, 8                  ; entered at 16k+8; two pushes + 8 = 16-byte aligned calls
    mov     rbx, rcx
    mov     r12, rdx
    call    memcpy wrt ..plt        ; rdi, rsi, rdx already hold dst, src, n; returns dst
    mov     rdi, rax
    mov     rsi, r12
    call    helper_checksum         ; a direct call: the helper is hidden, so no PLT is needed
    mov     [rbx], eax
    add     rsp, 8
    pop     r12
    pop     rbx
    ret
.end:
section .note.GNU-stack noalloc noexec nowrite progbits
```

`helper.asm`, in the same project:

```nasm
default rel
section .text
global helper_checksum:function hidden
helper_checksum:                    ; a byte sum, as a placeholder for the real checksum
    xor     eax, eax
    test    rsi, rsi
    jz      .done
.loop:
    movzx   ecx, byte [rdi]
    add     eax, ecx
    inc     rdi
    dec     rsi
    jnz     .loop
.done:
    ret
section .note.GNU-stack noalloc noexec nowrite progbits
```

**A function exported to .NET or Python without libc** (`freestanding`):

```nasm
default rel
section .text
; int64_t sum_i32(const int32_t *values, int64_t count)
global sum_i32:function (sum_i32.end - sum_i32)
sum_i32:
    xor     eax, eax
    test    rsi, rsi
    jle     .done
.loop:
    movsxd  rdx, dword [rdi]
    add     rax, rdx
    add     rdi, 4
    dec     rsi
    jnz     .loop
.done:
    ret
.end:
section .note.GNU-stack noalloc noexec nowrite progbits
```
