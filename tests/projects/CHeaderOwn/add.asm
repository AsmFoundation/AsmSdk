; add.asm - CHeaderOwn's one function: a + b, declared for C by the project's own header.

PROTO int32_t own_add(int32_t a, int32_t b)
FUNCTION own_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
