; duplicate.asm - ASM0134: two PROTO lines for one function.

PROTO int32_t twice(int32_t a)
PROTO int32_t twice(int32_t b)
FUNCTION twice, 1
    lea     eax, [ARG1 + ARG1]
    RETURN
ENDFUNCTION
