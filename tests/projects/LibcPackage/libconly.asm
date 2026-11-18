; =============================================================================
; libconly.asm - libtestlibconly (LibcPackage test project): a shared library
; that calls into glibc, so it can only serve libc projects.
;     size_t libconly_length(const char *text);
; =============================================================================

default rel
bits 64

extern strlen

section .text

; -----------------------------------------------------------------------------
; libconly_length - the length of a NUL-terminated string, from glibc's strlen.
; @param  rdi  text
; @returns rax = strlen(text)
; -----------------------------------------------------------------------------
global libconly_length:function
libconly_length:
    jmp     strlen wrt ..plt        ; tail call into glibc

section .note.GNU-stack noalloc noexec nowrite progbits
