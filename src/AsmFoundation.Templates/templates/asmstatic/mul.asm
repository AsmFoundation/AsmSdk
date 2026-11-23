; mul.asm - the x64 code of AsmStatic.1 (win-x64, linux-x64, linux-musl-x64, osx-x64).
;
; The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
; HIDDEN_FUNCTION, ARG1 .. ARG6, RETURN and ENDFUNCTION.
; https://asmfoundation.github.io/AsmSdk/guide/writing-nasm.html

; int32_t mul_ints(int32_t a, int32_t b): a * b, wrapping like C#'s unchecked multiplication.
; HIDDEN_FUNCTION: global for the code that links this library, exported by no image.
HIDDEN_FUNCTION mul_ints, 2
    mov     eax, ARG1d
    imul    eax, ARG2d
    RETURN
ENDFUNCTION
