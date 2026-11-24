// proto.wasm.s - a PROTO line inside a function body, where it would take the rest of the body
// into the record section: asmsdk-wasm.inc's guard stops it, pointing at line 5.

FUNCTION answer, () -> (i32)
PROTO int32_t answer(void)
    i32.const 42
ENDFUNCTION answer
