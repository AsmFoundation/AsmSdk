// sub.wasm.s - the WasmObjects test project: int wo_sub(int a, int b) = a - b, for both wasm RIDs.
FUNCTION wo_sub, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.sub
ENDFUNCTION wo_sub
