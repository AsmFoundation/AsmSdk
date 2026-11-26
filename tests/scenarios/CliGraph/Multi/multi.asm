; multi.asm - the x64 code of Multi (every x64 RID), for both of its target frameworks.
;
; The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
; HIDDEN_FUNCTION, ARG1 .. ARG6, RETURN and ENDFUNCTION.
; https://asmfoundation.github.io/AsmSdk/guide/writing-nasm.html

; int32_t sub_ints(int32_t a, int32_t b): a - b, wrapping like C#'s unchecked subtraction. Prog
; links the library without calling it: the static graph's handling of a freestanding;libc
; reference is what the scenario checks, not the code.
; HIDDEN_FUNCTION: global for the code that links this library, exported by no image.
HIDDEN_FUNCTION sub_ints, 2
    mov     eax, ARG1d
    sub     eax, ARG2d
    RETURN
ENDFUNCTION
