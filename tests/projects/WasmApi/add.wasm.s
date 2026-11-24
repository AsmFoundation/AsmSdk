// add.wasm.s - WasmApi's browser-wasm build: the same function as add.asm, with the same PROTO
// line, which every RID must declare alike (ASM0133). FUNCTION records the wasm type, and the
// SDK checks that the PROTO line lowers to exactly that type (ASM0132).

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add_ints
