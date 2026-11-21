; calc for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"

section .text

; int32_t calc_mul(int32_t a, int32_t b)
FUNCTION calc_mul, 2
    mov     eax, ARG1d
    imul    eax, ARG2d
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
