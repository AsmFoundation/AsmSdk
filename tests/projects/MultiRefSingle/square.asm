; =============================================================================
; square.asm - libmultisquare (MultiRefSingle test project).
;
; Exports one System V ABI function built on Hello.Static's static_mul, which
; the project reference links in from libhellostatic.a:
;     int multi_square(int x);
; =============================================================================

default rel
bits 64

%include "hello_static.inc"

section .text

; -----------------------------------------------------------------------------
; multi_square - returns x * x through static_mul (32-bit, wrapping).
; static_mul is a global symbol of this shared library once the archive member
; is linked in, so it is reached through the PLT: a direct PC32 call to a
; preemptible symbol is refused by the linker in a shared object.
; @param  edi  x
; @returns eax = x * x
; -----------------------------------------------------------------------------
global multi_square:function
multi_square:
    mov     esi, edi
    jmp     static_mul wrt ..plt    ; tail call: static_mul(x, x)

section .note.GNU-stack noalloc noexec nowrite progbits
