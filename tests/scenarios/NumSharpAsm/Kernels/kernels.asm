; kernels for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
; The result returns in xmm0 on every OS, and xmm0-xmm1 are scratch registers on every OS, so
; nothing needs saving (Windows wants xmm6-xmm15 back, which these functions never touch).
default rel
%include "asmsdk.inc"

section .text

; double kernels_sum(const double *values, size_t count)
FUNCTION kernels_sum, 2
    xorpd   xmm0, xmm0
    test    ARG2, ARG2
    jz      .done
.next:
    addsd   xmm0, [ARG1]
    add     ARG1, 8
    dec     ARG2
    jnz     .next
.done:
    RETURN
ENDFUNCTION

; double kernels_dot(const double *a, const double *b, size_t n)
FUNCTION kernels_dot, 3
    xorpd   xmm0, xmm0
    test    ARG3, ARG3
    jz      .done
.next:
    movsd   xmm1, [ARG1]
    mulsd   xmm1, [ARG2]
    addsd   xmm0, xmm1
    add     ARG1, 8
    add     ARG2, 8
    dec     ARG3
    jnz     .next
.done:
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
