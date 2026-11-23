; add.asm - the drift case's linux-x64 source: add_ints with two int32_t, which win-x64's
; add.win.asm declares differently (ASM0133, in the RID outer build).

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
