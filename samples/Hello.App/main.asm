; =============================================================================
; main.asm - hello-app: a freestanding executable (Hello.App sample).
;
; Calls static_mul(6, 7) from libhellostatic, compares the result with
; HELLO_STATIC_MAGIC from the library's public include file, prints one line
; and exits 0 when they match, 1 when they do not. Raw Linux system calls only.
; =============================================================================

default rel
bits 64

%include "hello_static.inc"

%define SYS_WRITE       1
%define SYS_EXIT_GROUP  231
%define STDOUT          1

section .rodata
message:        db "hello from a freestanding asm executable: 6 * 7 = 42", 10
message_len     equ $ - message

section .text

; -----------------------------------------------------------------------------
; _start - process entry (static executable: the kernel jumps here directly).
; Never returns; exit status 0 = the library answered HELLO_STATIC_MAGIC.
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

    xor     edi, edi                    ; status 0
    jmp     .exit
.wrong:
    mov     edi, 1                      ; status 1
.exit:
    mov     eax, SYS_EXIT_GROUP
    syscall

section .note.GNU-stack noalloc noexec nowrite progbits
