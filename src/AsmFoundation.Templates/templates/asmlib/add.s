// add.s - the ARM64 code of AsmLibrary.1 (win-arm64, linux-arm64, linux-musl-arm64, osx-arm64).
//
// The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
// FUNCTION and ENDFUNCTION export a function on every OS. Every ARM64 OS passes the integer
// arguments in x0..x7 (w0..w7 for 32 bits) and takes the result back in x0 (w0).
// https://asmfoundation.github.io/AsmSdk/guide/platforms.html

// int32_t add_ints(int32_t a, int32_t b): a + b, wrapping like C#'s unchecked addition.
FUNCTION add_ints
    add     w0, w0, w1
    ret
ENDFUNCTION add_ints
