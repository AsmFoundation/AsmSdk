// add.wasm.s - the same function without its PROTO line: browser-wasm declares nothing, so the
// RIDs disagree on add_ints (ASM0133), one API for every RID being the rule.

FUNCTION add_ints, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add_ints
