// echo.wasi.s - WasiEcho, a WASI command (wasi-wasm only): prints its arguments, then its
// environment, one string per line, and exits with 0. Shows what dotnet run hands a program it
// starts through wasmtime: "--" arguments, and the variables of dotnet run -e and of a launch
// profile, which wasmtime passes only when told to (--env).

// WASI preview 1: args and environ arrive as NUL-terminated strings, back to back in one
// buffer, with a separate array of pointers into it.
IMPORT_FUNCTION wasi_snapshot_preview1, args_sizes_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, args_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, environ_sizes_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, environ_get, (i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, fd_write, (i32, i32, i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, proc_exit, (i32) -> ()

// print_block(buffer, size): writes size bytes of buffer to standard output, each NUL turned
// into a line break first, so each string prints on a line of its own.
LOCAL_FUNCTION print_block, (i32, i32) -> ()
    .local i32                  // 2: the cursor
    local.get 0
    local.set 2
    block
    loop
        local.get 2
        local.get 0
        local.get 1
        i32.add
        i32.ge_u
        br_if 1                 // the end of the buffer
        local.get 2
        i32.load8_u 0
        i32.eqz
        if
            local.get 2
            i32.const 10        // '\n' for the NUL
            i32.store8 0
        end_if
        local.get 2
        i32.const 1
        i32.add
        local.set 2
        br 0
    end_loop
    end_block
    ADDRESS iov                 // iovec { buffer, size }
    local.get 0
    i32.store 0
    ADDRESS iov
    local.get 1
    i32.store 4
    i32.const 1                 // standard output
    ADDRESS iov
    i32.const 1
    ADDRESS written
    CALL fd_write
    drop                        // the errno: nothing to do about a failed write
ENDFUNCTION print_block

// fits() -> 1 when the counts the last *_sizes_get call stored fit the buffers below
// (64 pointers, 4096 bytes of strings), else 0: a bigger set is left unprinted, never overrun.
LOCAL_FUNCTION fits, () -> (i32)
    ADDRESS count
    i32.load 0
    i32.const 64
    i32.le_u
    ADDRESS size
    i32.load 0
    i32.const 4096
    i32.le_u
    i32.and
ENDFUNCTION fits

FUNCTION _start, () -> ()
    ADDRESS count
    ADDRESS size
    CALL args_sizes_get
    drop
    CALL fits
    if
        ADDRESS pointers
        ADDRESS strings
        CALL args_get
        drop
        ADDRESS strings
        ADDRESS size
        i32.load 0
        CALL print_block
    end_if
    ADDRESS count
    ADDRESS size
    CALL environ_sizes_get
    drop
    CALL fits
    if
        ADDRESS pointers
        ADDRESS strings
        CALL environ_get
        drop
        ADDRESS strings
        ADDRESS size
        i32.load 0
        CALL print_block
    end_if
    i32.const 0
    CALL proc_exit
ENDFUNCTION _start

BSS
// WASI checks that the pointers it is given are aligned (wasmtime: "Pointer not aligned to 4").
.p2align 2
iov:
    .skip 8
ENDDATA iov
written:
    .skip 4
ENDDATA written
count:
    .skip 4
ENDDATA count
size:
    .skip 4
ENDDATA size
pointers:
    .skip 256
ENDDATA pointers
strings:
    .skip 4096
ENDDATA strings
