; nofunction.asm - ASM0131 twice: a PROTO for a function no source defines (ghost), and one for a
; LOCAL_FUNCTION, which nothing outside this file can call (helper).

PROTO int32_t ghost(void)

PROTO int32_t helper(void)
LOCAL_FUNCTION helper
    xor     eax, eax
    RETURN
ENDFUNCTION
