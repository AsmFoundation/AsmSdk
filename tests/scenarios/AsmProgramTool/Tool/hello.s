// asm-hello for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

RODATA
greeting:
    .asciz "asm-hello: a .NET tool written in assembly"

// int main(int argc, char **argv): prints the greeting and argv[argc - 1], then returns 7.
// x19 and x20 (callee-saved) keep argc and argv across the calls, saved in the frame.
FUNCTION main
    stp     x29, x30, [sp, #-32]!
    mov     x29, sp
    stp     x19, x20, [sp, #16]
    mov     w19, w0                     // argc
    mov     x20, x1                     // argv
    ADDRESS x0, greeting
    CALL    puts
    sub     w2, w19, #1
    ldr     x0, [x20, w2, uxtw #3]      // argv[argc - 1]
    CALL    puts
    mov     w0, #7
    ldp     x19, x20, [sp, #16]
    ldp     x29, x30, [sp], #32
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
