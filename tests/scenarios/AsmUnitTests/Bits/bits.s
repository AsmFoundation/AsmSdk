// bits for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// int32_t bits_popcount(uint64_t x)
// ARM64 has no scalar population count before FEAT_CSSC: the value goes to a vector
// register, CNT counts the bits of each byte, and ADDV adds the eight counts. Writing b0
// clears the rest of the register, so s0 holds just the sum.
FUNCTION bits_popcount
    fmov    d0, x0
    cnt     v0.8b, v0.8b
    addv    b0, v0.8b
    fmov    w0, s0
    ret
ENDFUNCTION bits_popcount

// int32_t bits_leading_zeros(uint64_t x)
// CLZ answers 64 for 0 by itself.
FUNCTION bits_leading_zeros
    clz     x0, x0
    ret
ENDFUNCTION bits_leading_zeros

// uint64_t bits_rotate_left(uint64_t x, int32_t n)
// ARM64 rotates right only: left by n is right by -n. ROR uses the count modulo 64, and
// writing w1 clears the upper half of x1, so -n mod 64 is what it reads.
FUNCTION bits_rotate_left
    neg     w1, w1
    ror     x0, x0, x1
    ret
ENDFUNCTION bits_rotate_left

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
