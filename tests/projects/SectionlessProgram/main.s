// main.s - the ARM64 RIDs of SectionlessProgram: no section directive and no include. GNU
// syntax needs no declaration of sl_mul: a name this file does not define is external, and the
// linker finds it in the Sectionless static library.

// int main(void): sl_mul(6, 7), so the program exits with status 42. The call overwrites x30
// (the return address), so it is saved with x29 (the frame pointer).
FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     w0, #6
    mov     w1, #7
    CALL    sl_mul
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main
