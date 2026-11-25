; mul.asm - BindingsPackageB's x64 RIDs: a * b, the low 32 bits.

PROTO int32_t mul_ints(int32_t a, int32_t b)
FUNCTION mul_ints, 2
    mov     eax, ARG1d
    imul    eax, ARG2d
    RETURN
ENDFUNCTION
