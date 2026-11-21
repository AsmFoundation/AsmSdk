// greet for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

RODATA
message:
    .asciz "greet: hello from an asm program"

// int main(void): prints the message, then returns calc_mul(6, 7) from the shared library.
FUNCTION main
    stp     x29, x30, [sp, #-16]!   // the frame record: bl overwrites x30
    mov     x29, sp
    ADDRESS x0, message
    CALL    puts
    mov     w0, #6
    mov     w1, #7
    CALL    calc_mul                // resolved at run time, from the library next to the program
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
