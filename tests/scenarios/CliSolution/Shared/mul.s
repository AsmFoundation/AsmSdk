// mul.s - the ARM64 code of Shared (win-arm64, linux-arm64, linux-musl-arm64, osx-arm64).
//
// The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
// HIDDEN_FUNCTION and ENDFUNCTION. https://asmfoundation.github.io/AsmSdk/guide/platforms.html

// int32_t mul_ints(int32_t a, int32_t b): a * b, wrapping like C#'s unchecked multiplication.
// HIDDEN_FUNCTION: global for the code that links this library, exported by no image.
HIDDEN_FUNCTION mul_ints
    mul     w0, w0, w1
    ret
ENDFUNCTION mul_ints
