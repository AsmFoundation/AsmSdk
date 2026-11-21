// asm-helper for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// int main(int argc, char **argv): prints argv[argc - 1], then returns 40 + argc.
// x19 (callee-saved) keeps argc across the call; it is saved in the frame with x29 and x30.
FUNCTION main
    stp     x29, x30, [sp, #-32]!
    mov     x29, sp
    str     x19, [sp, #16]
    mov     w19, w0                     // argc
    sub     w2, w0, #1
    ldr     x0, [x1, w2, uxtw #3]       // argv[argc - 1]
    CALL    puts
    add     w0, w19, #40
    ldr     x19, [sp, #16]
    ldp     x29, x30, [sp], #32
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
