; count.asm - ASM0132: three integer parameters, but FUNCTION says two (on Windows the count
; decides which arguments are loaded from the stack, so they must agree).

PROTO int32_t add3(int32_t a, int32_t b, int32_t c)
FUNCTION add3, 2
    lea     eax, [ARG1 + ARG2]
    add     eax, ARG3d
    RETURN
ENDFUNCTION
