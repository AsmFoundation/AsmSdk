// callback.s - ApiProto's ARM64 RIDs: calls a function pointer, op(a, b), as callback.asm.

PROTO int32_t apply(int32_t (*op)(int32_t, int32_t), int32_t a, int32_t b)
FUNCTION apply
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     x3, x0
    mov     w0, w1
    mov     w1, w2
    blr     x3
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION apply
