; add.asm - linux-x64 declares add_ints for the bindings; browser-wasm's add.wasm.s does not.

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
