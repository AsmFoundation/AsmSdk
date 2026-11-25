; add.asm - BindingsPackage's x64 RIDs: a + b, in a root file of the library.

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
