// add.wasm.s - WasmApi's browser-wasm build: the same function as add.asm, without its PROTO
// line, which asmsdk-wasm.inc refuses (WebAssembly RIDs declare nothing to the bindings yet).

FUNCTION add_ints, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add_ints
