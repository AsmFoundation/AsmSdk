; ImageFixups, win-x64: no "default rel" on purpose. The lea below addresses message
; absolutely (IMAGE_REL_AMD64_ADDR32), which lld-link links into the DLL as a HIGHLOW base
; relocation that cannot hold an address above 4 GB: ASM0080.
%include "asmsdk.inc"

section .rodata
message:    db "absolute", 0

section .text
FUNCTION fixup_address
    lea     rax, [message]
    RETURN
ENDFUNCTION
