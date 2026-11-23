; badtype.asm - ASM0130: C's long is 32-bit on Windows and 64-bit elsewhere, so a binding of it
; could not be one class for every RID; the message names the fixed-width types instead.

PROTO long bad_type(long x)
FUNCTION bad_type, 1
    mov     rax, ARG1
    RETURN
ENDFUNCTION
