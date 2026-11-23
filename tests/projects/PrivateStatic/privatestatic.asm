; privatestatic.asm - PrivateStatic's one RID, linux-x64: a function that calls the static
; library linked into this shared library. sl_mul is hidden in the archive, so the call is
; direct, through no PLT.
extern sl_mul

; int32_t ps_square(int32_t x): x * x. A call needs rsp 16-byte aligned, and the return address
; left it 8 bytes off: the extra 8 align it.
FUNCTION ps_square, 1
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG2d, ARG1d
    call    sl_mul
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
