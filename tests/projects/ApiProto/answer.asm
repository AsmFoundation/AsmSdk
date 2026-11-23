; answer.asm - ApiProto's x64 RIDs other than win-x64 (which takes answer.win.asm): one logical
; file, "answer", with a variant per platform, each declaring the same PROTO.

PROTO int32_t answer(void)
FUNCTION answer
    mov     eax, 42
    RETURN
ENDFUNCTION
