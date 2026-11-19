; The x64 RIDs: strlen, malloc, memset and free from the platform's C library.
default rel
%include "asmsdk.inc"
extern strlen, malloc, free, memset

section .rodata
text:   db "assembly", 0

section .text
FUNCTION elc_len
    sub     rsp, SHADOW_SPACE + 8
    lea     ARG1, [text]
    call    strlen PLT
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; rbx holds the block across the calls; pushing it leaves rsp 16-aligned for SHADOW_SPACE.
FUNCTION elc_heap, 0, rbx
    sub     rsp, SHADOW_SPACE
    mov     ARG1d, 64
    call    malloc PLT
    test    rax, rax
    jz      .fail
    mov     rbx, rax
    mov     ARG1, rax
    mov     ARG2d, 0x5a
    mov     ARG3d, 64
    call    memset PLT
    movzx   eax, byte [rbx + 63]
    cmp     eax, 0x5a
    jne     .fail
    mov     ARG1, rbx
    call    free PLT
    mov     eax, 1
    jmp     .done
.fail:
    xor     eax, eax
.done:
    add     rsp, SHADOW_SPACE
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
