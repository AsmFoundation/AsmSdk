; =============================================================================
; exports.asm - libexports (Exports test project).
;
; Two global functions; the version script (exports.map) keeps only the first in
; the dynamic symbol table:
;     int exported_fn(void);     exported, versioned EXPORTS_1.0
;     int internal_fn(void);     global in the object, local in the library
; =============================================================================

default rel
bits 64

section .text

; -----------------------------------------------------------------------------
; exported_fn - the library's only export.
; The call to internal_fn is a direct call. That links because the version
; script makes internal_fn local, so nothing can preempt it (-Bsymbolic alone
; would do the same). The PLT form is not an option here: NASM 3.02 stops with
; "panic: intra-segment OUT_REL4ADR" on "call internal_fn wrt ..plt" when the
; target is in the same section of the same file.
; @returns eax = internal_fn() + 1 = 8
; -----------------------------------------------------------------------------
global exported_fn:function
exported_fn:
    call    internal_fn
    inc     eax
    ret

; -----------------------------------------------------------------------------
; internal_fn - global in the object file, hidden by the version script.
; @returns eax = 7
; -----------------------------------------------------------------------------
global internal_fn:function
internal_fn:
    mov     eax, 7
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
