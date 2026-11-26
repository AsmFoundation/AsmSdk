// main.s - WatchProgram for the ARM64 RIDs: prints the line message-gnu.inc defines (see
// main.asm), and exits with 0.
.include "asmsdk-gnu.inc"
.include "message-gnu.inc"

RODATA
message:
    MESSAGE_TEXT

.text

// int main(void): one call, so the frame record alone keeps sp aligned.
FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ADDRESS x0, message
    CALL    puts
    mov     w0, #0
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
