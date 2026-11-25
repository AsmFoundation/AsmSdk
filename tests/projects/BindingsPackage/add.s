// add.s - BindingsPackage's ARM64 RIDs: the same PROTO line as add.asm, which every RID must agree on.

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints
    add     w0, w0, w1
    ret
ENDFUNCTION add_ints
