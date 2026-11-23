; add.win.asm - the drift case's win-x64 variant: its second parameter drifted to int64_t, which
; the bindings (one class for every RID) cannot follow: ASM0133.

PROTO int32_t add_ints(int32_t a, int64_t b)
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
