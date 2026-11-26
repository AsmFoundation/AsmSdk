// main.s - CliGraph's ChainProg for the ARM64 RIDs: square_ints from the static library Chain (see main.asm).
// No declaration: a name this file does not define is external, and the linker finds it in
// Chain's static library (and mul_ints, which Chain calls, in Shared's).

// int main(void): square_ints(6) + 6, so the program exits with status 42.
FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     w0, #6
    CALL    square_ints
    add     w0, w0, #6
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main
