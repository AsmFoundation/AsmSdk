; Expected to FAIL: line 7 is no instruction (NasmError.asmproj). Keep it on line 7.
default rel
%include "asmsdk.inc"

section .text
FUNCTION ne_answer, 0
    movx    eax, 42
    RETURN
ENDFUNCTION

section .note.GNU-stack noalloc noexec nowrite progbits
