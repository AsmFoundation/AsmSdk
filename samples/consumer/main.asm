; =============================================================================
; main.asm - hello-consumer: uses the Hello.Static package (by version).
;
; Same check as hello-app, but the include file and the static library come
; from the NuGet package instead of a project reference. Exit 0 on success.
; =============================================================================

default rel
bits 64

%include "hello_static.inc"

%define SYS_WRITE       1
%define SYS_EXIT_GROUP  231
%define STDOUT          1

section .rodata
message:        db "hello from the Hello.Static package: 6 * 7 = 42", 10
message_len     equ $ - message

section .text

; -----------------------------------------------------------------------------
; _start - process entry. Never returns; exit status 0 = expected answer.
; -----------------------------------------------------------------------------
global _start:function
_start:
    mov     edi, 6
    mov     esi, 7
    call    static_mul
    cmp     eax, HELLO_STATIC_MAGIC
    jne     .wrong

    mov     eax, SYS_WRITE
    mov     edi, STDOUT
    lea     rsi, [message]
    mov     edx, message_len
    syscall

    xor     edi, edi
    jmp     .exit
.wrong:
    mov     edi, 1
.exit:
    mov     eax, SYS_EXIT_GROUP
    syscall

section .note.GNU-stack noalloc noexec nowrite progbits
