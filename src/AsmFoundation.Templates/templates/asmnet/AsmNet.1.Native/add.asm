; add.asm - the x64 code of AsmNet.1.Native (win-x64, linux-x64, linux-musl-x64, osx-x64).
;
; The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
;   FUNCTION name, n   starts a function that every OS exports; n integer arguments
;   ARG1 .. ARG6       the argument registers of this OS's calling convention
;                      (rcx, rdx, r8, r9 on Windows; rdi, rsi, rdx, rcx on Linux and macOS),
;                      with 32-bit forms ARG1d, ARG2d, ...
;   RETURN             returns (and restores what FUNCTION saved)
; so the same code builds for every x64 platform.
; https://asmfoundation.github.io/AsmSdk/guide/writing-nasm.html

; int32_t add_ints(int32_t a, int32_t b): a + b, wrapping like C#'s unchecked addition.
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
