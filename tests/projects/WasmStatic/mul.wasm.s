// mul.wasm.s - the WasmStatic test project: int ws_mul(int a, int b) = a * b, for both wasm RIDs.
FUNCTION ws_mul, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.mul
ENDFUNCTION ws_mul
