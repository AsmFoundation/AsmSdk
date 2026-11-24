// twice.wasm.s - a PROTO line whose wasm lowering is not FUNCTION's type: ASM0132 at line 4.
// int64_t lowers to i64, so the prototype is (i64) -> (i64), and the function takes an i32.

PROTO int64_t twice(int64_t v)
FUNCTION twice, (i32) -> (i32)
    local.get 0
    local.get 0
    i32.add
ENDFUNCTION twice
