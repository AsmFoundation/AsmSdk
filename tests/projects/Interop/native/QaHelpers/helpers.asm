; helpers.asm - QaHelpers, x64: int64_t qa_helper_square(int64_t x), global within the image
; that links it and exported by none.
default rel
%include "asmsdk.inc"

section .text
HIDDEN_FUNCTION qa_helper_square, 1
    mov     rax, ARG1
    imul    rax, rax
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
