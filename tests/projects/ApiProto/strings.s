// strings.s - ApiProto's ARM64 RIDs: the length of a NUL-terminated string, as strings.asm.

PROTO size_t name_length(const char* name)
FUNCTION name_length
    mov     x1, x0
1:  ldrb    w2, [x1], #1
    cbnz    w2, 1b
    sub     x0, x1, x0
    sub     x0, x0, #1
    ret
ENDFUNCTION name_length
