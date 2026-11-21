// The ARM64 RIDs of EverywhereProgram (and of EverywherePackageProgram, which assembles this
// file too): win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// int main(int argc, char **argv): prints argv[argc - 1], then returns
// 10 * ew_platform() + argc - 1 (see program.asm).
FUNCTION main
    stp     x29, x30, [sp, #-32]!   // the frame record, and room for x19 and x20
    mov     x29, sp
    stp     x19, x20, [sp, #16]
    mov     w19, w0                 // argc
    sub     w2, w0, #1
    ldr     x0, [x1, w2, uxtw #3]   // argv[argc - 1]
    CALL    puts
    CALL    ew_platform             // from the shared library, found at run time
    mov     w2, #10
    mul     w0, w0, w2
    add     w0, w0, w19
    sub     w0, w0, #1              // + the number of arguments
    ldp     x19, x20, [sp, #16]
    ldp     x29, x30, [sp], #32
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
