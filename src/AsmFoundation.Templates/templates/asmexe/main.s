// main.s - the ARM64 code of AsmApp.1 (win-arm64, linux-arm64, linux-musl-arm64, osx-arm64).
//
// The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
// FUNCTION and ENDFUNCTION, RODATA for read-only data, ADDRESS to load a label's address and
// CALL to call the C library by its name on this OS.
// https://asmfoundation.github.io/AsmSdk/guide/platforms.html

RODATA
greeting:
    .asciz  "Hello from assembly!"

// int main(void): prints the greeting with the C library's puts and returns 0.
// The call overwrites x30 (the return address), so it is saved with x29 (the frame pointer).
FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ADDRESS x0, greeting
    CALL    puts
    mov     w0, #0
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main
