; =============================================================================
; mul.asm - libhellostatic (Hello.Static sample).
; =============================================================================

default rel
bits 64

section .text

; -----------------------------------------------------------------------------
; static_mul - returns a * b (32-bit, wrapping).
; @param  edi  a
; @param  esi  b
; @returns eax = a * b
; -----------------------------------------------------------------------------
global static_mul:function
static_mul:
    mov     eax, edi
    imul    eax, esi
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
