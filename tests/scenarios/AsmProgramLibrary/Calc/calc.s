// calc for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// int32_t calc_mul(int32_t a, int32_t b)
FUNCTION calc_mul
    mul     w0, w0, w1
    ret
ENDFUNCTION calc_mul

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
