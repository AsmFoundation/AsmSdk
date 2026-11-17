; =============================================================================
; main.asm - hello-libc: an assembly program on glibc (Hello.Libc sample).
;
;     int main(int argc, char **argv)
;
; Prints one line with printf and returns 0. __libc_start_main (called by the
; SDK's crt1) passes the return value to exit(), which flushes stdout.
; =============================================================================

default rel
bits 64

extern printf

section .rodata
format:         db "hello from %s: %d arguments", 10, 0
who:            db "an asm main() on glibc", 0

section .text

; -----------------------------------------------------------------------------
; main - program entry, called by __libc_start_main.
; @param  edi  argc
; @param  rsi  argv
; @returns eax = 0
; @remarks printf is variadic: al must hold the number of vector registers used
;          (0 here). The push keeps the stack 16-byte aligned at the call, as
;          the ABI requires (main is entered with rsp = 16n + 8).
; -----------------------------------------------------------------------------
global main:function
main:
    push    rbx
    mov     edx, edi                    ; 3rd printf argument: argc
    lea     rdi, [format]
    lea     rsi, [who]
    xor     eax, eax                    ; no vector arguments
    call    printf wrt ..plt
    xor     eax, eax
    pop     rbx
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
