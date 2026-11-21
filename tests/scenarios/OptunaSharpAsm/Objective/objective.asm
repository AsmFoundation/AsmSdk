; objective for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"

section .rodata
two:    dq 2.0
one:    dq 1.0

section .text

; double objective_eval(double x, double y) = (x - 2)^2 + (y + 1)^2
; Two double arguments arrive in xmm0 and xmm1 on every OS (Windows numbers floating-point
; arguments by position, System V in order: for two doubles both agree), and the result
; returns in xmm0. Plain multiplies and adds, no FMA, so the result equals C#'s bit for bit.
FUNCTION objective_eval
    subsd   xmm0, [two]         ; x - 2
    mulsd   xmm0, xmm0
    addsd   xmm1, [one]         ; y + 1
    mulsd   xmm1, xmm1
    addsd   xmm0, xmm1
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
