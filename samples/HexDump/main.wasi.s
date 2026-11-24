// =============================================================================
// main.wasi.s - hexdump: an xxd-style hex dump, as a WASI command (HexDump
// sample: the wasi-wasm build of main.asm).
//
//     hexdump [file]        (no file, or "-": standard input)
//
// The same lines as main.asm prints, xxd's default format:
//     00000000: 4865 6c6c 6f2c 2077 6f72 6c64 210a       Hello, world!.
// an offset of at least 8 hex digits, 16 bytes as 8 groups of 2, then the
// bytes as text, with "." for anything outside printable ASCII.
// Exit status: 0 success, 1 the file could not be opened, read or written,
// 2 a usage error.
//
// It shows a WASI command: the host (wasmtime, Node's WASI, an edge platform)
// enters it at _start, and it reaches the outside world only through the
// wasi_snapshot_preview1 functions it imports. What main.asm asks of the kernel
// with syscall, this asks of the host with call:
//     openat   -> path_open     read  -> fd_read     write -> fd_write
//     exit_group -> proc_exit   argc/argv on the stack -> args_sizes_get, args_get
// WASI has no ambient file system: a program opens files only under the
// folders the host preopened, as file descriptors from 3 up. "dotnet run" and
// "wasmtime run --dir ." give the current folder as fd 3, so a relative name
// opens there; an absolute name, or one that leaves that folder, is refused
// (wasmtime 39 answers EPERM, "Operation not permitted", measured), and with no
// folder preopened at all fd 3 does not exist (EBADF). A directory opens like a
// file under WASI and fails only when read (EBADF, measured), so the program
// asks for the file's type first and says "Is a directory", as main.asm does.
// WASI has no signals, so nothing here retries EINTR as main.asm does; writes
// may still be short, and write_all finishes them.
//
// No assembler constants exist in WebAssembly (asmsdk-wasm.inc's footguns), so
// the sizes are written where they are used, each with its name:
//     IN_SIZE  65536   the input buffer, a multiple of 16: whole lines per read
//     OUT_SIZE 65536   the output buffer
//     LINE_MAX 84      the longest line (a 16-digit offset ": " 40 hex " " 16
//                      text "\n" is 76): flush when fewer bytes are left
// =============================================================================

IMPORT_FUNCTION wasi_snapshot_preview1, args_sizes_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, args_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, path_open, (i32, i32, i32, i32, i32, i64, i64, i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, fd_filestat_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, fd_read, (i32, i32, i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, fd_write, (i32, i32, i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, proc_exit, (i32) -> ()

RODATA
hex_digits:
    .ascii "0123456789abcdef"
ENDDATA hex_digits
name_stdin:
    .asciz "-"
ENDDATA name_stdin
msg_prefix:
    .ascii "hexdump: "
ENDDATA msg_prefix
msg_usage:
    .ascii "usage: hexdump [file]\n"
ENDDATA msg_usage
// The reasons, for WASI's errno values (wasi_snapshot_preview1): the words
// main.asm prints for Linux's, and WASI's own for what only WASI refuses.
msg_enoent:
    .asciz "No such file or directory"
ENDDATA msg_enoent
msg_eacces:
    .asciz "Permission denied"
ENDDATA msg_eacces
msg_eisdir:
    .asciz "Is a directory"
ENDDATA msg_eisdir
msg_enotcapable:
    .asciz "Capabilities insufficient"
ENDDATA msg_enotcapable
msg_eperm:
    .asciz "Operation not permitted"
ENDDATA msg_eperm
msg_nopreopen:
    .asciz "No folder preopened: run it with --dir ."
ENDDATA msg_nopreopen
msg_error:
    .asciz "error "
ENDDATA msg_error

BSS
.p2align 4
in_buf:
    .skip 65536
ENDDATA in_buf
out_buf:
    .skip 65536
ENDDATA out_buf
err_buf:
    .skip 512
ENDDATA err_buf
// WASI checks that the pointers it is given are aligned (wasmtime: "Pointer not
// aligned to 4"), so the words below start at a multiple of 4.
.p2align 2
iov:                    // one iovec: { buffer, length }
    .skip 8
ENDDATA iov
result:                 // where a WASI call stores its count or descriptor
    .skip 4
ENDDATA result
args_size:              // the bytes argv's strings need
    .skip 4
ENDDATA args_size
out_len:                // the bytes waiting in out_buf
    .skip 4
ENDDATA out_len
argv:                   // argv[0..1], and its null: argc <= 2 is checked first
    .skip 12
ENDDATA argv
.p2align 3
filestat:               // fd_filestat_get's answer: the file type is the byte at 16
    .skip 64
ENDDATA filestat
arg_buf:                // argv's strings
    .skip 65536
ENDDATA arg_buf

// -----------------------------------------------------------------------------
// copy_string(dst, src, max) -> the byte after the copy: copies a
// NUL-terminated string without its NUL, at most max bytes. The name comes from
// the command line, so the caller caps it to fit err_buf.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION copy_string, (i32, i32, i32) -> (i32)
    .local i32                  // 3: the byte
    block
    loop
        local.get 2
        i32.eqz
        br_if 1                 // max bytes copied
        local.get 1
        i32.load8_u 0
        local.tee 3
        i32.eqz
        br_if 1                 // the NUL
        local.get 0
        local.get 3
        i32.store8 0
        local.get 0
        i32.const 1
        i32.add
        local.set 0
        local.get 1
        i32.const 1
        i32.add
        local.set 1
        local.get 2
        i32.const 1
        i32.sub
        local.set 2
        br 0
    end_loop
    end_block
    local.get 0
ENDFUNCTION copy_string

// -----------------------------------------------------------------------------
// put_decimal(dst, value) -> the byte after the digits: writes an unsigned
// number in decimal. Counts the digits first, then writes them from the last,
// where main.asm pushes them below the stack pointer: a wasm function has no
// stack of bytes of its own to borrow.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION put_decimal, (i32, i32) -> (i32)
    .local i32, i32             // 2: the digit count, 3: the rest, then the cursor
    i32.const 1
    local.set 2
    local.get 1
    local.set 3
    block
    loop
        local.get 3
        i32.const 10
        i32.lt_u
        br_if 1
        local.get 3
        i32.const 10
        i32.div_u
        local.set 3
        local.get 2
        i32.const 1
        i32.add
        local.set 2
        br 0
    end_loop
    end_block
    local.get 0
    local.get 2
    i32.add
    local.set 3                 // one past the last digit
    loop
        local.get 3
        i32.const 1
        i32.sub
        local.tee 3
        local.get 1
        i32.const 10
        i32.rem_u
        i32.const 48            // '0'
        i32.add
        i32.store8 0
        local.get 1
        i32.const 10
        i32.div_u
        local.tee 1
        br_if 0                 // more digits while something is left
    end_loop
    local.get 0
    local.get 2
    i32.add
ENDFUNCTION put_decimal

// -----------------------------------------------------------------------------
// string_length(s) -> the bytes before its NUL: path_open takes a length, not a
// C string.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION string_length, (i32) -> (i32)
    .local i32                  // 1: the cursor
    local.get 0
    local.set 1
    block
    loop
        local.get 1
        i32.load8_u 0
        i32.eqz
        br_if 1
        local.get 1
        i32.const 1
        i32.add
        local.set 1
        br 0
    end_loop
    end_block
    local.get 1
    local.get 0
    i32.sub
ENDFUNCTION string_length

// -----------------------------------------------------------------------------
// write_all(fd, bytes, length): writes a whole buffer, however many calls the
// host needs: a write may take fewer bytes than asked (a pipe), and the rest
// goes in the next call. Exits with status 1 when the write fails.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION write_all, (i32, i32, i32) -> ()
    .local i32                  // 3: the bytes one call wrote
    block
    loop
        local.get 2
        i32.eqz
        br_if 1                 // all written
        ADDRESS iov
        local.get 1
        i32.store 0
        ADDRESS iov
        local.get 2
        i32.store 4
        local.get 0
        ADDRESS iov
        i32.const 1             // one iovec
        ADDRESS result
        CALL fd_write
        if                      // an errno: the output is gone
            i32.const 1
            CALL proc_exit
            unreachable
        end_if
        ADDRESS result
        i32.load 0
        local.set 3
        local.get 1
        local.get 3
        i32.add
        local.set 1
        local.get 2
        local.get 3
        i32.sub
        local.set 2
        br 0
    end_loop
    end_block
ENDFUNCTION write_all

// -----------------------------------------------------------------------------
// flush(): writes out_buf's waiting bytes to standard output and empties it.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION flush, () -> ()
    i32.const 1                 // standard output
    ADDRESS out_buf
    ADDRESS out_len
    i32.load 0
    CALL write_all
    ADDRESS out_len
    i32.const 0
    i32.store 0
ENDFUNCTION flush

// -----------------------------------------------------------------------------
// dump_line(bytes, count, offset): formats one line into out_buf, flushing
// first when the longest line might not fit.
// @param bytes   the line's bytes
// @param count   how many (1-16)
// @param offset  the line's offset in the input (i64: inputs past 4 GiB)
// -----------------------------------------------------------------------------
LOCAL_FUNCTION dump_line, (i32, i32, i64) -> ()
    .local i32, i32, i32, i32, i32  // 3: the output cursor, 4: the digit shift,
                                    // 5: the byte index, 6: the byte, 7: its column
    ADDRESS out_len
    i32.load 0
    i32.const 65452             // OUT_SIZE - LINE_MAX
    i32.gt_u
    if
        CALL flush
    end_if
    ADDRESS out_buf
    ADDRESS out_len
    i32.load 0
    i32.add
    local.set 3

    // The offset: 8 hex digits, more when it needs them (xxd's %08lx): above
    // 32 bits, ceil(significant bits / 4) digits.
    i32.const 8
    local.set 4
    local.get 2
    i64.const 32
    i64.shr_u
    i64.eqz
    i32.eqz
    if
        i64.const 67            // 64 - clz + 3, then / 4
        local.get 2
        i64.clz
        i64.sub
        i64.const 2
        i64.shr_u
        i32.wrap_i64
        local.set 4
    end_if
    local.get 4                 // the first digit's shift: digits * 4 - 4
    i32.const 2
    i32.shl
    i32.const 4
    i32.sub
    local.set 4
    loop
        local.get 3
        ADDRESS hex_digits
        local.get 2
        local.get 4
        i64.extend_i32_u
        i64.shr_u
        i32.wrap_i64
        i32.const 15
        i32.and
        i32.add
        i32.load8_u 0
        i32.store8 0
        local.get 3
        i32.const 1
        i32.add
        local.set 3
        local.get 4
        i32.const 4
        i32.sub
        local.tee 4
        i32.const 0
        i32.ge_s
        br_if 0
    end_loop
    local.get 3
    i32.const 8250              // ": " (0x3a, then 0x20: little-endian 0x203a)
    i32.store16 0
    local.get 3
    i32.const 2
    i32.add
    local.set 3

    // The hex area: 40 columns, blank first, so that a partial last line keeps
    // the text column in place; a space after every second byte.
    local.get 3
    i32.const 32                // ' '
    i32.const 40
    memory.fill 0
    i32.const 0
    local.set 5
    block
    loop
        local.get 5
        local.get 1
        i32.ge_u
        br_if 1
        local.get 0
        local.get 5
        i32.add
        i32.load8_u 0
        local.set 6
        local.get 5             // column: (i / 2) * 5 + (i % 2) * 2
        i32.const 1
        i32.shr_u
        i32.const 5
        i32.mul
        local.get 5
        i32.const 1
        i32.and
        i32.const 1
        i32.shl
        i32.add
        local.get 3
        i32.add
        local.tee 7
        ADDRESS hex_digits
        local.get 6
        i32.const 4
        i32.shr_u
        i32.add
        i32.load8_u 0
        i32.store8 0            // the high digit
        local.get 7
        ADDRESS hex_digits
        local.get 6
        i32.const 15
        i32.and
        i32.add
        i32.load8_u 0
        i32.store8 1            // the low digit, one column on
        local.get 5
        i32.const 1
        i32.add
        local.set 5
        br 0
    end_loop
    end_block
    local.get 3
    i32.const 40
    i32.add
    local.tee 3
    i32.const 32                // ' '
    i32.store8 0
    local.get 3
    i32.const 1
    i32.add
    local.set 3

    // The text: printable ASCII (0x20-0x7e) as itself, anything else as '.'.
    i32.const 0
    local.set 5
    loop
        local.get 0
        local.get 5
        i32.add
        i32.load8_u 0
        local.set 6
        // The byte if printable, else '.': an if with a result rather than select,
        // whose untyped form llvm-mc 23.1.5's type checker refuses ("type mismatch,
        // expected [i32] but got [i32, i32]", measured on a bare three-operand select).
        local.get 3
        local.get 6
        i32.const 32
        i32.sub
        i32.const 94            // 0x7e - 0x20
        i32.le_u
        if i32
            local.get 6
        else
            i32.const 46        // '.'
        end_if
        i32.store8 0
        local.get 3
        i32.const 1
        i32.add
        local.set 3
        local.get 5
        i32.const 1
        i32.add
        local.tee 5
        local.get 1
        i32.lt_u
        br_if 0
    end_loop
    local.get 3
    i32.const 10                // '\n'
    i32.store8 0
    ADDRESS out_len             // out_len = the cursor + 1 - out_buf
    local.get 3
    i32.const 1
    i32.add
    ADDRESS out_buf
    i32.sub
    i32.store 0
ENDFUNCTION dump_line

// -----------------------------------------------------------------------------
// fail_with(errno, name): "hexdump: <name>: <reason>" on standard error, then
// exit 1.
// @param errno  WASI's errno, from the failed call
// @param name   the name, a C string (cut at 400 bytes to fit err_buf)
// -----------------------------------------------------------------------------
LOCAL_FUNCTION fail_with, (i32, i32) -> ()
    .local i32, i32             // 2: the output cursor, 3: the reason (0: written)
    ADDRESS err_buf
    ADDRESS msg_prefix
    i32.const 9                 // "hexdump: "
    memory.copy 0, 0
    ADDRESS err_buf
    i32.const 9
    i32.add
    local.get 1
    i32.const 400
    CALL copy_string
    local.tee 2
    i32.const 8250              // ": "
    i32.store16 0
    local.get 2
    i32.const 2
    i32.add
    local.set 2

    block
        ADDRESS msg_enoent
        local.set 3
        local.get 0
        i32.const 44            // NOENT
        i32.eq
        br_if 0
        ADDRESS msg_eacces
        local.set 3
        local.get 0
        i32.const 2             // ACCES
        i32.eq
        br_if 0
        ADDRESS msg_eisdir
        local.set 3
        local.get 0
        i32.const 31            // ISDIR
        i32.eq
        br_if 0
        ADDRESS msg_enotcapable
        local.set 3
        local.get 0
        i32.const 76            // NOTCAPABLE
        i32.eq
        br_if 0
        ADDRESS msg_eperm
        local.set 3
        local.get 0
        i32.const 63            // PERM: outside the preopened folder (wasmtime)
        i32.eq
        br_if 0
        ADDRESS msg_nopreopen
        local.set 3
        local.get 0
        i32.const 8             // BADF: only path_open gives it here, when fd 3,
        i32.eq                  // the preopened folder, does not exist
        br_if 0
        // Any other errno: its number, as main.asm prints Linux's.
        local.get 2
        ADDRESS msg_error
        i32.const 400
        CALL copy_string
        local.get 0
        CALL put_decimal
        local.set 2
        i32.const 0
        local.set 3
    end_block
    local.get 3
    if
        local.get 2
        local.get 3
        i32.const 400
        CALL copy_string
        local.set 2
    end_if
    local.get 2
    i32.const 10                // '\n'
    i32.store8 0
    i32.const 2                 // standard error
    ADDRESS err_buf
    local.get 2
    i32.const 1
    i32.add
    ADDRESS err_buf
    i32.sub
    CALL write_all
    i32.const 1
    CALL proc_exit
    unreachable
ENDFUNCTION fail_with

// -----------------------------------------------------------------------------
// usage(): the usage line on standard error, then exit 2.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION usage, () -> ()
    i32.const 2                 // standard error
    ADDRESS msg_usage
    i32.const 22                // "usage: hexdump [file]\n"
    CALL write_all
    i32.const 2
    CALL proc_exit
    unreachable
ENDFUNCTION usage

// -----------------------------------------------------------------------------
// _start: the WASI command's entry.
// -----------------------------------------------------------------------------
FUNCTION _start, () -> ()
    .local i32, i32, i32, i32, i64, i32 // 0: the fd, 1: the name, 2: argc, then the
                                        // bytes in in_buf, 3: the line cursor,
                                        // 4: in_buf[0]'s offset, 5: errno or count
    ADDRESS result
    ADDRESS args_size
    CALL args_sizes_get
    drop
    ADDRESS result
    i32.load 0
    local.tee 2
    i32.const 2
    i32.gt_u
    if
        CALL usage              // more than one argument
    end_if
    i32.const 0                 // standard input
    local.set 0
    ADDRESS name_stdin
    local.set 1
    local.get 2
    i32.const 2
    i32.eq
    if
        ADDRESS args_size       // a name that needs more than arg_buf: usage
        i32.load 0
        i32.const 65536
        i32.gt_u
        if
            CALL usage
        end_if
        ADDRESS argv
        ADDRESS arg_buf
        CALL args_get
        drop
        ADDRESS argv
        i32.load 4              // argv[1]
        local.tee 1
        i32.load16_u 0
        i32.const 45            // "-" and its NUL: standard input
        i32.ne
        if
            // path_open(fd 3: the folder the host preopened, follow symlinks,
            //           the name, its length, no oflags, rights: fd_read (bit
            //           1), none to inherit, no fdflags, where the fd goes)
            i32.const 3
            i32.const 1
            local.get 1
            local.get 1
            CALL string_length
            i32.const 0
            i64.const 2
            i64.const 0
            i32.const 0
            ADDRESS result
            CALL path_open
            local.tee 5
            if
                local.get 5
                local.get 1
                CALL fail_with
            end_if
            ADDRESS result
            i32.load 0
            local.set 0
            // A directory opens like a file and fails only when read (EBADF):
            // its type says so first, for main.asm's message.
            local.get 0
            ADDRESS filestat
            CALL fd_filestat_get
            drop                // a failure here shows at the first read
            ADDRESS filestat
            i32.load8_u 16
            i32.const 3         // DIRECTORY
            i32.eq
            if
                i32.const 31    // ISDIR
                local.get 1
                CALL fail_with
            end_if
        end_if
    end_if

    i32.const 0
    local.set 2                 // the bytes waiting in in_buf
    i64.const 0
    local.set 4
    block                       // the end of the input
    loop                        // one read
        ADDRESS iov
        ADDRESS in_buf
        local.get 2
        i32.add
        i32.store 0
        ADDRESS iov
        i32.const 65536         // IN_SIZE: the room after the waiting bytes
        local.get 2
        i32.sub
        i32.store 4
        local.get 0
        ADDRESS iov
        i32.const 1
        ADDRESS result
        CALL fd_read
        local.tee 5
        if
            local.get 5
            local.get 1
            CALL fail_with
        end_if
        ADDRESS result
        i32.load 0
        local.tee 5
        i32.eqz
        br_if 1                 // nothing read: the end
        local.get 2
        local.get 5
        i32.add
        local.set 2

        // Every whole line in the buffer; the partial one waits for more.
        i32.const 0
        local.set 3
        block
        loop
            local.get 2
            local.get 3
            i32.sub
            i32.const 16
            i32.lt_u
            br_if 1
            ADDRESS in_buf
            local.get 3
            i32.add
            i32.const 16
            local.get 4
            CALL dump_line
            local.get 3
            i32.const 16
            i32.add
            local.set 3
            local.get 4
            i64.const 16
            i64.add
            local.set 4
            br 0
        end_loop
        end_block
        // The 0-15 leftover bytes move to the front.
        ADDRESS in_buf
        ADDRESS in_buf
        local.get 3
        i32.add
        local.get 2
        local.get 3
        i32.sub
        local.tee 2
        memory.copy 0, 0
        br 0
    end_loop
    end_block

    local.get 2                 // the last, partial line
    if
        ADDRESS in_buf
        local.get 2
        local.get 4
        CALL dump_line
    end_if
    CALL flush
    i32.const 0
    CALL proc_exit
ENDFUNCTION _start
