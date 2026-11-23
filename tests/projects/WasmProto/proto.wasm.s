// proto.wasm.s - a PROTO line where WebAssembly cannot carry one yet: line 3 must fail.

PROTO int32_t answer(void)
FUNCTION answer, () -> (i32)
    i32.const 42
ENDFUNCTION answer
