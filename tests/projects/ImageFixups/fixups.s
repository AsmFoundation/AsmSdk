// ImageFixups, osx-arm64: the address of message stored in __text itself (a literal next to
// the code), which needs a load-time fixup in a read-only segment: ASM0081, from the rebase
// opcodes, or from LC_DYLD_CHAINED_FIXUPS when linked with -fixup_chains.
.include "asmsdk-gnu.inc"

RODATA
message:    .asciz "absolute"

FUNCTION fixup_address
    ldr     x0, literal
    ret
    .p2align 3
literal:
    .quad   message
ENDFUNCTION fixup_address
