// libc.wasm.s - the WasmLibc test project: one function that says whether LIBC was defined (an
// archive for a .NET WebAssembly app would call the C library its build links).
FUNCTION wc_is_libc, () -> (i32)
.ifdef LIBC
    i32.const 1
.else
    i32.const 0
.endif
ENDFUNCTION wc_is_libc
