; answer.win.asm - ApiProto's win-x64 variant of answer.asm (the closer qualifier wins): the same
; PROTO line, so every RID agrees, from another file.

PROTO int32_t answer(void)
FUNCTION answer
    mov     eax, 42
    RETURN
ENDFUNCTION
