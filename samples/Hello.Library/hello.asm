; =============================================================================
; hello.asm - libhello: a freestanding shared library (Hello.Library sample).
;
; Exports two System V ABI functions that a C or .NET caller can use:
;     int hello_add(int a, int b);
;     int hello_version(void);
; The code is position-independent (default rel), as a shared object requires.
; =============================================================================

default rel
bits 64

%include "hello.inc"

section .text

; -----------------------------------------------------------------------------
; hello_add - returns a + b (32-bit, wrapping).
; @param  edi  a
; @param  esi  b
; @returns eax = a + b
; -----------------------------------------------------------------------------
global hello_add:function
hello_add:
    lea     eax, [rdi + rsi]
    ret

; -----------------------------------------------------------------------------
; hello_version - returns HELLO_VERSION (major << 16 | minor).
; @returns eax = HELLO_VERSION
; -----------------------------------------------------------------------------
global hello_version:function
hello_version:
    mov     eax, HELLO_VERSION
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
