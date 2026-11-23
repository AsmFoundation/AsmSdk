; lib.asm - PackSources, x64: packed under src/ with AsmPackSources.
%include "pub.inc"
%include "private.inc"

; int32_t ps_answer(void): PS_ANSWER from the public include file.
FUNCTION ps_answer, 0
    mov     eax, PS_ANSWER + PS_PRIVATE_OFFSET
    RETURN
ENDFUNCTION
