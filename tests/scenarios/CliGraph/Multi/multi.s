// multi.s - the ARM64 code of Multi (every ARM64 RID), for both of its target frameworks.
//
// The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
// HIDDEN_FUNCTION and ENDFUNCTION. https://asmfoundation.github.io/AsmSdk/guide/platforms.html

// int32_t sub_ints(int32_t a, int32_t b): a - b, wrapping like C#'s unchecked subtraction.
// HIDDEN_FUNCTION: global for the code that links this library, exported by no image.
HIDDEN_FUNCTION sub_ints
    sub     w0, w0, w1
    ret
ENDFUNCTION sub_ints
