// sum.s - BindingsPackage's ARM64 RIDs: the byte sum of a buffer, as simd/sum.asm.

PROTO fast uint32_t sum_bytes(const uint8_t* data, size_t len)
FUNCTION sum_bytes
    mov     w2, #0
    cbz     x1, 2f
1:  ldrb    w3, [x0], #1
    add     w2, w2, w3
    subs    x1, x1, #1
    b.ne    1b
2:  mov     w0, w2
    ret
ENDFUNCTION sum_bytes
