// main.s - CliBuild's program for the ARM64 RIDs: mul_ints from the static library Shared (see main.asm).
// no declaration: a name this file does not define is external, and the linker finds it in
// Shared's static library.

// int main(void): mul_ints(6, 7), so the program exits with status 42.
FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     w0, #6
    mov     w1, #7
    CALL    mul_ints
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main
