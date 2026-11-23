; add.asm - WasmApi's linux-x64 build: the PROTO line the C# bindings read.

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
