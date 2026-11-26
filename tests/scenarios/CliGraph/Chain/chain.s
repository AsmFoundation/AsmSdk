// chain.s - the ARM64 code of Chain (win-arm64, linux-arm64, linux-musl-arm64, osx-arm64).
//
// The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
// HIDDEN_FUNCTION, CALL and ENDFUNCTION. No declaration of mul_ints: a name this file does not
// define is external, and the linker finds it in Shared's static library.

// int32_t square_ints(int32_t a): mul_ints(a, a), from Shared, so a program that links this
// library links Shared too.
// HIDDEN_FUNCTION: global for the code that links this library, exported by no image.
HIDDEN_FUNCTION square_ints
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     w1, w0
    CALL    mul_ints
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION square_ints
