; asm-hello for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"
extern puts

section .rodata
greeting:   db "asm-hello: a .NET tool written in assembly", 0

section .text

; int main(int argc, char **argv): prints the greeting and argv[argc - 1], then returns 7.
; rbx and rbp (listed, so FUNCTION saves them) keep argc and argv across the calls; after the
; two pushes rsp is 16n+8, and SHADOW_SPACE + 8 aligns it for the calls.
FUNCTION main, 2, rbx, rbp
    sub     rsp, SHADOW_SPACE + 8
    mov     ebx, ARG1d                  ; argc
    mov     rbp, ARG2                   ; argv
    lea     ARG1, [greeting]
    call    puts PLT
    movsxd  rax, ebx
    mov     ARG1, [rbp + rax*8 - 8]     ; argv[argc - 1]
    call    puts PLT
    mov     eax, 7
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
