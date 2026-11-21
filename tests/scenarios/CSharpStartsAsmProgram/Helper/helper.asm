; asm-helper for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"
extern puts

section .text

; int main(int argc, char **argv): prints argv[argc - 1], then returns 40 + argc.
; FUNCTION saves rbx (listed), which keeps argc across the call; that push also leaves rsp
; 16-byte aligned, and SHADOW_SPACE (32 on Windows, 0 elsewhere) keeps it so.
FUNCTION main, 2, rbx
    sub     rsp, SHADOW_SPACE
    mov     ebx, ARG1d                  ; argc
    movsxd  rax, ebx
    mov     ARG1, [ARG2 + rax*8 - 8]    ; argv[argc - 1]
    call    puts PLT
    lea     eax, [rbx + 40]
    add     rsp, SHADOW_SPACE
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
