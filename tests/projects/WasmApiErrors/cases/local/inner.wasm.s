// inner.wasm.s - a PROTO line for a LOCAL_FUNCTION, which no module exports: ASM0131 at line 3.

PROTO int32_t inner(void)
LOCAL_FUNCTION inner, () -> (i32)
    i32.const 42
ENDFUNCTION inner

// A FUNCTION too, so that the library exports something and the case is only about inner.
FUNCTION outer, () -> (i32)
    i32.const 42
ENDFUNCTION outer
