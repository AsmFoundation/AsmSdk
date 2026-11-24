// shape.wasm.s - a STRUCT line where WebAssembly cannot carry one yet: line 3 must fail.

STRUCT shape, int32_t width, int32_t height
FUNCTION answer, () -> (i32)
    i32.const 42
ENDFUNCTION answer
