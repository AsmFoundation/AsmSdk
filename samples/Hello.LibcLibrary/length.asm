; =============================================================================
; length.asm - libhellolibc: a shared library on glibc (Hello.LibcLibrary).
;
;     size_t hello_length(const char *text);   returns strlen(text) * 2
; =============================================================================

default rel
bits 64

extern strlen

section .text

; -----------------------------------------------------------------------------
; hello_length - twice the length of a NUL-terminated string, via libc strlen.
; @param  rdi  text (NUL-terminated)
; @returns rax = strlen(text) * 2
; @remarks The tail is a plain call through the PLT: the stub libc.so.6 the SDK
;          links against made the linker create it, and glibc's strlen fills it
;          at load time. The push/pop keeps the stack 16-byte aligned for strlen.
; -----------------------------------------------------------------------------
global hello_length:function
hello_length:
    push    rbx
    call    strlen wrt ..plt
    add     rax, rax
    pop     rbx
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
