; linux-x64 of SystemLd: int sl_answer(void) = 42. Never assembled by the tests (the build
; stops at ASM0005 first), present so that every listed RID has a source.
default rel
%include "asmsdk.inc"

section .text
FUNCTION sl_answer, 0
    mov     eax, 42
    RETURN
ENDFUNCTION

section .note.GNU-stack noalloc noexec nowrite progbits
