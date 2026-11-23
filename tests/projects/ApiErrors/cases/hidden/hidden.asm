; hidden.asm - a PROTO for a HIDDEN_FUNCTION: ASM0131 in a shared library (no image exports it),
; bindable in a static library, whose users link it (a NativeAOT app among them).

PROTO int32_t inner_answer(void)
HIDDEN_FUNCTION inner_answer
    mov     eax, 42
    RETURN
ENDFUNCTION
