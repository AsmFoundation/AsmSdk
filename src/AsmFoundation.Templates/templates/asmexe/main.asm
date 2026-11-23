; main.asm - the x64 code of AsmApp.1 (win-x64, linux-x64, linux-musl-x64, osx-x64).
;
; The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
; FUNCTION, ARG1 .. ARG6, RETURN and ENDFUNCTION, and SHADOW_SPACE and PLT for calls into the
; C library. https://asmfoundation.github.io/AsmSdk/guide/writing-nasm.html

extern puts

section .rodata
greeting:   db "Hello from assembly!", 0

section .text

; int main(void): prints the greeting with the C library's puts and returns 0.
; A call needs rsp 16-byte aligned, and the return address left it 8 bytes off: the extra 8
; align it. SHADOW_SPACE is the 32 bytes a callee may use on Windows (0 elsewhere).
FUNCTION main
    sub     rsp, SHADOW_SPACE + 8
    lea     ARG1, [greeting]
    call    puts PLT
    xor     eax, eax
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
