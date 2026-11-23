// main.s - the ARM64 RIDs of SectionlessRawProgram: ImplicitUsings is disabled, so the source
// includes asmsdk-gnu.inc itself, and names no section before its functions. GNU syntax needs no
// declaration of raw_mul: a name this file does not define is external.
.include "asmsdk-gnu.inc"

// int main(void): raw_mul(6, seven()), so the program exits with status 42. The calls overwrite
// x30 (the return address), so it is saved with x29 (the frame pointer).
FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    CALL    seven
    mov     w1, w0
    mov     w0, #6
    CALL    raw_mul
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main

// int seven(void): 7, a file-local function.
LOCAL_FUNCTION seven
    mov     w0, #7
    ret
ENDFUNCTION seven
