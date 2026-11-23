; ImageFixups, win-x64: absolute addressing on purpose. The lea below addresses message
; absolutely (IMAGE_REL_AMD64_ADDR32), which lld-link links into the DLL as a HIGHLOW base
; relocation that cannot hold an address above 4 GB: ASM0080. ImplicitUsings (on by default)
; puts "default rel" in front of every source, which makes the lea RIP-relative and the DLL
; correct; "default abs" here wins over it, as a source that wants absolute addresses writes.
%include "asmsdk.inc"
default abs

section .rodata
message:    db "absolute", 0

section .text
FUNCTION fixup_address
    lea     rax, [message]
    RETURN
ENDFUNCTION
