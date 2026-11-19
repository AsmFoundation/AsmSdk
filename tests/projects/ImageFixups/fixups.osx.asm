; ImageFixups, osx-x64: a 64-bit absolute address in code (mov rax, label), which ld64.lld
; links as a rebase entry in __TEXT, a segment dyld maps read-only: ASM0081.
default rel
%include "asmsdk.inc"

section .rodata
message:    db "absolute", 0

section .text
FUNCTION fixup_address
    mov     rax, message
    RETURN
ENDFUNCTION
