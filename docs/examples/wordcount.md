# WordCount: a tool on libc

[`samples/WordCount`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/WordCount) is
`wc` in assembly: it counts the lines, words and bytes of files. It is an ordinary C-style
program, `main()` on glibc, that happens to be written in NASM, and it shows what such a program
needs from AsmSdk: calls into libc, a libc function the SDK's stub does not list, a libc *data*
object, and two source files that share a record.

```text
$ wordcount notes.txt empty.txt
       3       6      28 notes.txt
       0       0       0 empty.txt
       3       6      28 total
$ wordcount -l < notes.txt
       3
$ wordcount missing.txt; echo $?
wordcount: missing.txt: No such file or directory
1
```

| Usage | Meaning |
|---|---|
| `wordcount [-lwc] [file ...]` | `-l`, `-w`, `-c` select lines, words or bytes (also as `-lw`); without options all three |
| `--`, `-` | `--` ends the options; `-` reads standard input; no file at all reads standard input and prints no name |
| exit status | 0 success; 1 a file could not be read (the others are still counted); 2 a usage error; 3 the CPU lacks POPCNT |

## The project

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <TargetFramework>libc</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>wordcount</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <AsmLibcSymbol Include="warn" />
    <AsmLibcSymbol Include="warnx" />
  </ItemGroup>
</Project>
```

- **`TargetFramework=libc`** builds a PIE that needs `libc.so.6`, started by the SDK's `crt1`
  through `__libc_start_main`, so the program is a plain `main(argc, argv)`. See
  [Runtimes and linking](../guide/runtimes-and-linking.md#libc-programs-and-libraries).
- **`AsmLibcSymbol`:** the build links against a generated `libc.so.6` stub that lists about
  290 functions. `warn` and `warnx` (from `<err.h>`) are not among them, so without these two
  items the link fails with `undefined symbol: warn`. At run time the real glibc provides them.

## Calling libc

Every libc function is called through the PLT, and the program follows the System V rules a C
compiler would:

```nasm
extern open, read, close, printf, fputs, warn, warnx, stderr

    mov     esi, O_RDONLY
    xor     eax, eax                    ; open is variadic: no vector arguments
    call    open wrt ..plt
```

- **Variadic functions** (`open`, `printf`, `warn`) need `al` = the number of vector registers
  used for arguments: 0 here, so `xor eax, eax` before each call.
- **The stack is 16-byte aligned at every call.** `main` is entered with `rsp` = 16n + 8, so it
  pushes six callee-saved registers and reserves 8 more bytes; each helper's comment states the
  same arithmetic.
- **`errno` without touching it:** `warn("%s", name)` prints
  `wordcount: name: No such file or directory`, reading `errno` itself.

**`stderr` is a variable, not a function.** It lives in glibc, so the program reads its address
from the GOT, then the `FILE *` it holds:

```nasm
    lea     rdi, [usage]
    mov     rax, [rel stderr wrt ..got] ; &stderr, from the GOT
    mov     rsi, [rax]                  ; stderr itself: a FILE *
    call    fputs wrt ..plt
```

## Two files, one record

`main.asm` reads the files and prints; `count.asm` counts. They share the layout of the counts
through `wordcount.inc`, with NASM's `struc`, the way two C files share a header:

```nasm
struc Counts
    .lines:     resq 1
    .words:     resq 1
    .bytes:     resq 1
    .space:     resq 1          ; 1 when the last byte seen was whitespace
endstruc
```

The SDK assembles every `.asm` file of the project and links them together; the include folder
is the project folder, so `%include "wordcount.inc"` needs no setting.

## Counting 16 bytes at a time

`count_buffer` classifies 16 bytes per step with SSE2. Two compares produce one bit per byte,
and `pmovmskb` moves those 16 bits into a general register:

```nasm
    movdqa  xmm1, xmm0
    pcmpeqb xmm1, xmm5                  ; byte == '\n'
    pmovmskb eax, xmm1                  ; newline mask
    movdqa  xmm2, xmm0
    pcmpeqb xmm2, xmm6                  ; byte == ' '
    movdqa  xmm3, xmm0
    psubb   xmm3, xmm7                  ; byte - 9, wrapping
    movdqa  xmm4, xmm3
    pminub  xmm4, xmm8                  ; min(byte - 9, 4)
    pcmpeqb xmm3, xmm4                  ; byte - 9 <= 4: \t \n \v \f \r
    por     xmm2, xmm3
    pmovmskb ecx, xmm2                  ; whitespace mask
```

Then lines are `popcnt(newlines)`, and words are the non-space bytes whose previous byte is a
space: `popcnt(~ws & ((ws << 1) | space_before_block))`. A block costs the same however many
words it holds.

- **A word that spans two reads counts once.** The "previous byte was a space" bit is carried
  from block to block and from one 64 KiB read to the next, in `Counts.space`.
- **The last, partial block** is copied into 16 spaces in the red zone, the 128 bytes below
  `rsp` that the System V ABI reserves for leaf functions, so it needs no stack frame. Spaces add
  no newline and start no word; the carried bit is then taken from the last *real* byte.
- **SSE2 is part of x86-64; POPCNT is not** (it is x86-64-v2). `main` checks for it with
  `cpuid` and exits with status 3 and a message, instead of dying of `SIGILL`.

**Words follow POSIX:** a word is a run of bytes that are not C-locale whitespace (space, `\t`,
`\n`, `\v`, `\f`, `\r`). On text, the counts equal GNU `wc`'s. On binary data GNU `wc` counts
fewer words, because it also ignores bytes that are not printable.

## How it is checked

[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs):

- **The imports:** `libc.so.6` is needed; `open`, `read`, `close`, `printf`, `fputs`, `warn` and
  `warnx` are undefined functions, and `stderr` an object.
- **The counts:** three files (a short text, an empty file, and 200,000 bytes holding a 100,000-byte
  word across the read boundary and every whitespace byte) against a C# count, with the total
  line; then `-lw`, and `-c` on standard input.
- **The errors:** a missing file between good ones (exit 1, the message, the others counted) and
  an unknown option (exit 2, the usage).
