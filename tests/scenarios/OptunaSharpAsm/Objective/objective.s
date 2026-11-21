// objective for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// double objective_eval(double x, double y) = (x - 2)^2 + (y + 1)^2
// x in d0, y in d1, the result in d0. fmov takes 2.0 and 1.0 as immediates. fmul then fadd,
// not fmadd, so the result equals C#'s bit for bit.
FUNCTION objective_eval
    fmov    d2, #2.0
    fsub    d0, d0, d2          // x - 2
    fmul    d0, d0, d0
    fmov    d2, #1.0
    fadd    d1, d1, d2          // y + 1
    fmul    d1, d1, d1
    fadd    d0, d0, d1
    ret
ENDFUNCTION objective_eval

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
