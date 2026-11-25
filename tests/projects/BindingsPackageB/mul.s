// mul.s - BindingsPackageB's ARM64 RIDs: the same PROTO line as mul.asm.

PROTO int32_t mul_ints(int32_t a, int32_t b)
FUNCTION mul_ints
    mul     w0, w0, w1
    ret
ENDFUNCTION mul_ints
