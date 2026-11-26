// add.s - the ARM64 code of Lib (win-arm64, linux-arm64, linux-musl-arm64, osx-arm64).
//
// The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
// FUNCTION and ENDFUNCTION export a function on every OS. Every ARM64 OS passes the integer
// arguments in x0..x7 (w0..w7 for 32 bits) and takes the result back in x0 (w0). PROTO is the
// function's C prototype, the same line as in add.asm: every platform must declare the same.
// https://asmfoundation.github.io/AsmSdk/guide/platforms.html

// a + b, wrapping like C#'s unchecked addition.
PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints
    add     w0, w0, w1
    ret
ENDFUNCTION add_ints
