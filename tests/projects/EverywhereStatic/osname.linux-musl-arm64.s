// Exactly linux-musl-arm64 (a RID qualifier). Not osname.linux-musl.s: a qualified .s applies
// to every RID its qualifier names, x64 ones too (as GNU-syntax x86-64), so the linux-musl
// family would claim linux-musl-x64 as well and tie with osname.linux-musl.asm (ASM0020,
// measured).
.include "asmsdk-gnu.inc"
HIDDEN_FUNCTION osname
    mov     w0, #22
    ret
ENDFUNCTION osname
