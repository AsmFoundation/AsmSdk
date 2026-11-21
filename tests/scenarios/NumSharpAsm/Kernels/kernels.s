// kernels for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
// The result returns in d0; d0-d2 are scratch registers (only d8-d15 must be kept).
.include "asmsdk-gnu.inc"

// double kernels_sum(const double *values, size_t count)
FUNCTION kernels_sum
    movi    d0, #0
    cbz     x1, 2f
1:  ldr     d1, [x0], #8
    fadd    d0, d0, d1
    subs    x1, x1, #1
    b.ne    1b
2:  ret
ENDFUNCTION kernels_sum

// double kernels_dot(const double *a, const double *b, size_t n)
// fmul then fadd, not fmadd: the same rounding as the x64 code and as C#.
FUNCTION kernels_dot
    movi    d0, #0
    cbz     x2, 2f
1:  ldr     d1, [x0], #8
    ldr     d2, [x1], #8
    fmul    d1, d1, d2
    fadd    d0, d0, d1
    subs    x2, x2, #1
    b.ne    1b
2:  ret
ENDFUNCTION kernels_dot

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
