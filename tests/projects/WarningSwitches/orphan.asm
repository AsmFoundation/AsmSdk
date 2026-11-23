; orphan.asm - one NASM warning on purpose: lonely_label below has no colon, which NASM reports
; as label-orphan ("label alone on a line without a colon might be in error").

FUNCTION ws_answer, 0
    mov     eax, 42
    RETURN
ENDFUNCTION

lonely_label
