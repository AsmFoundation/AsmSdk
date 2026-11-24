; byvalue.asm - StructErrors: the PROTO on line 5 must fail (ASM0130): a struct passed by value
; travels differently on each ABI, so PROTO takes a pointer to it.

STRUCT point, int32_t x, int32_t y
PROTO int32_t norm(point p)
FUNCTION norm, 1
    xor     eax, eax
    RETURN
ENDFUNCTION
