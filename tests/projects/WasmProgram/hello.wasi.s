// hello.wasi.s - a WASI command (the wasi qualifier: wasi-wasm only). It writes one line to
// standard output and exits with wl_add(40, 2), a function of the linked WasmLibrary archive.

// WASI preview 1, the interface wasmtime, Node and every WASI host provide to a command:
//   fd_write(fd, iovs, iovs_len, nwritten) -> errno
//   proc_exit(code), which does not return
IMPORT_FUNCTION wasi_snapshot_preview1, fd_write, (i32, i32, i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, proc_exit, (i32) -> ()
EXTERN_FUNCTION wl_add, (i32, i32) -> (i32)

// The entry point WASI hosts call (the SDK links the program with it as its entry).
FUNCTION _start, () -> ()
    i32.const 1          // standard output
    ADDRESS iov          // one iovec: { message, length }
    i32.const 1
    ADDRESS written      // where fd_write stores the byte count
    CALL fd_write
    drop                 // the errno: nothing to do about a failed write here
    i32.const 40
    i32.const 2
    CALL wl_add
    CALL proc_exit
ENDFUNCTION _start

    DATA
// struct iovec { const void *base; size_t length; }: wasm32 pointers and sizes are 32 bits.
// Aligned to 4: llvm-mc aligns no data by itself, and wasmtime refuses a WASI call whose
// pointers are not ("Pointer not aligned to 4", measured; Node's WASI did not check).
    .p2align 2
iov:
    .int message
    .int .Lmessage_end - message
ENDDATA iov
written:
    .int 0
ENDDATA written

    RODATA
message:
    .ascii "hello from WebAssembly\n"
.Lmessage_end:
ENDDATA message
