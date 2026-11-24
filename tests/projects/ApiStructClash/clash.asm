; clash.asm - ApiStructClash: a struct named like this file, and a function that reads it.

STRUCT clash, uint32_t value

; uint32_t clash_value(const clash* c): c->value.
PROTO uint32_t clash_value(const clash* c)
FUNCTION clash_value, 1
    mov     eax, [ARG1 + clash.value]
    RETURN
ENDFUNCTION
