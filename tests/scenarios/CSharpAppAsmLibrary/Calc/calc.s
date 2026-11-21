// calc for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// int32_t calc_add(int32_t a, int32_t b)
FUNCTION calc_add
    add     w0, w0, w1
    ret
ENDFUNCTION calc_add

// int64_t calc_sum(const int32_t *values, size_t count)
// ldrsw sign-extends each value to 64 bits before it is added.
FUNCTION calc_sum
    mov     x2, x0
    mov     x0, #0
    cbz     x1, 2f
1:  ldrsw   x3, [x2], #4
    add     x0, x0, x3
    subs    x1, x1, #1
    b.ne    1b
2:  ret
ENDFUNCTION calc_sum

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
