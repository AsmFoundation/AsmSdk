; =============================================================================
; main.asm - libc-consumer (LibcConsumer test project): calls libconly_length
; from the Test.LibcOnly package and exits with the length it returns.
;     int main(void);
; =============================================================================

default rel
bits 64

%include "libconly.inc"

section .rodata
text:   db "eleven char", 0         ; strlen = 11

section .text

; -----------------------------------------------------------------------------
; main - entered from the SDK's crt1 through __libc_start_main.
; @returns eax = libconly_length("eleven char") = 11, the exit status that
;          RunTests (tests/AsmSdk.Tests) expects
; -----------------------------------------------------------------------------
global main:function
main:
    sub     rsp, 8                  ; keep rsp 16-byte aligned at the call
    lea     rdi, [text]
    call    libconly_length wrt ..plt
    add     rsp, 8
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
