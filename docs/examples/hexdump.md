# HexDump: a tool without libc

[`samples/HexDump`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/HexDump) prints
a hex dump in `xxd`'s default format, and links nothing: no libc, no loader, no other library.
The kernel starts it at `_start`, and it talks to the kernel with the `syscall` instruction only.
The executable is about 4 KB, static, and runs on any x86-64 Linux.

```text
$ printf 'Hello, world!\n' | hexdump
00000000: 4865 6c6c 6f2c 2077 6f72 6c64 210a       Hello, world!.
$ hexdump /tmp; echo $?
hexdump: /tmp: Is a directory
1
```

| Usage | Meaning |
|---|---|
| `hexdump [file]` | dumps the file; no file, or `-`, dumps standard input |
| output | the same bytes `xxd` prints, for every input: an offset of at least 8 hex digits, 8 groups of 2 bytes, the bytes as text with `.` for anything outside printable ASCII |
| exit status | 0 success; 1 the file could not be opened, read or written; 2 a usage error |

## The project

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>hexdump</AssemblyName>
  </PropertyGroup>
</Project>
```

No `TargetFramework` means `freestanding`: the SDK links the program `-static` with `_start` as
its entry, and the result needs nothing at run time.

| ELF fact | Value |
|---|---|
| Type | `EXEC (Executable file)` |
| Interpreter (`PT_INTERP`), `DT_NEEDED` | none |
| Size | about 3.8 KB (the exact size depends on the source path NASM records) |

## Entered by the kernel

There is no `main` and no `argc` parameter: the kernel leaves them on the stack.

```nasm
_start:
    xor     r12d, r12d                  ; standard input
    lea     r13, [name_stdin]
    mov     rax, [rsp]                  ; argc (after the xor: it would clear the flags)
    cmp     rax, 2
    ja      usage_error
    jb      .start                      ; argc 1: no argument
    mov     rdi, [rsp + 16]             ; argv[1]
```

`[rsp]` is `argc`, `[rsp + 8]` is `argv[0]`, `[rsp + 16]` is `argv[1]`. A program without libc
also has to end itself: `exit_group`, not `exit`, which would end only the calling thread.

## Doing what libc would do

Without libc, the program has to handle what `fread`, `fwrite` and `perror` hide:

- **Interrupted calls:** a signal that arrives during `read`, `write` or `openat` makes the call
  return `-EINTR` (system calls return `-errno` in `rax`). The program tries again.
- **Short writes:** `write` may take fewer bytes than asked, for a pipe for example. The rest
  goes in the next call:

```nasm
write_all:
    test    rdx, rdx
    jz      .done
    mov     eax, SYS_WRITE
    syscall                             ; clobbers rcx and r11
    cmp     rax, -EINTR
    je      write_all
    test    rax, rax
    js      .failed
    add     rsi, rax
    sub     rdx, rax
    jmp     write_all
```

- **Buffered output:** lines go into a 64 KiB buffer that is flushed when nearly full and at the
  end, so a large file takes a few hundred `write` calls rather than one per line.
- **Messages:** `ENOENT`, `EACCES` and `EISDIR` get their usual text; any other error is printed
  as `error <number>`.
- **`syscall` clobbers `rcx` and `r11`,** so nothing is kept in them across a system call: the
  line formatter saves its byte count around a flush.

## Matching xxd

Each line is built in place: the offset, `": "`, a 40-column hex area that is blanked first (so a
short last line keeps the text column aligned), one more space, and the text. The offset has 8
digits until 4 GiB and more after, as `xxd`'s `%08lx` does.

**Compared with xxd itself:** empty input, 1, 15, 16 and 17 bytes, all 256 byte values, 300,000
random bytes (1.27 MB of output), standard input and a pipe gave byte-identical output; so did
offsets past 4 GiB, with a build that starts at 0xFFFFFFF0 against `xxd -o 0xFFFFFFF0`.

## How it is checked

[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs):

- **The file:** `EXEC`, no interpreter, no `DT_NEEDED`, under 8 KB.
- **The output:** all 256 byte values and a partial last line, from a file and from standard
  input, against a C# rendering of xxd's format.
- **The errors:** a missing file (exit 1, `hexdump: no-such-file: No such file or directory`) and
  two arguments (exit 2, the usage).

## The WASI build

The project lists a second runtime, `wasi-wasm`, and a second source for it, `main.wasi.s`: the
same tool as a WASI command, about 2.5 KB, which wasmtime (or any WASI host) runs, and
`dotnet run -r wasi-wasm` starts there.

```xml
<RuntimeIdentifiers>linux-x64;wasi-wasm</RuntimeIdentifiers>
```

```text
$ dotnet run -r wasi-wasm -- photo.jpg
00000000: ffd8 ffe0 0010 4a46 4946 0001 0101 0048  ......JFIF.....H
$ wasmtime run --dir . hexdump.wasm sub; echo $?
hexdump: sub: Is a directory
1
```

What `main.asm` asks of the kernel with `syscall`, `main.wasi.s` asks of the host with `call`, through
the `wasi_snapshot_preview1` functions it imports: `path_open` for `openat`, `fd_read` and
`fd_write` for `read` and `write`, `proc_exit` for `exit_group`, and `args_sizes_get` with
`args_get` for the arguments the kernel would have put on the stack. The formatting is the same
algorithm, written for a stack machine.

- **No ambient file system.** A WASI program opens files only under the folders its host
  preopened, as file descriptors from 3 up; `dotnet run` and `wasmtime run --dir .` give the
  current folder as fd 3. A name outside it is refused (wasmtime 39 answers EPERM,
  `Operation not permitted`). With no folder preopened, the tool says how to run it:
  `No folder preopened: run it with --dir .`.
- **A directory opens.** Under WASI a directory opens like a file and fails only when read
  (EBADF), so the tool asks for the file's type first (`fd_filestat_get`) and says
  `Is a directory`, as `main.asm` does.
- **No signals,** so nothing retries EINTR. Writes may still be short; `write_all` finishes them.
- **No assembler constants** in WebAssembly, so the buffer sizes are written where they are used.
- **Compared with xxd itself:** all 256 byte values, 200,000 random bytes, an empty input, from a
  file, from standard input and from `-`: byte-identical.

`ExampleTests` checks the module (a WASI command under 4 KB that imports WASI alone), the output
(a 200,000-byte input crosses the 64 KiB read buffer and the output flushes), every error with
its words and exit status, and `dotnet run -r wasi-wasm`. The checks run wherever wasmtime is on
`PATH`, which CI installs on all five machines.
