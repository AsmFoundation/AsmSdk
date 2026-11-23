; sectionless.asm - the x64 RIDs of Sectionless: no section directive and no include, so the
; first line below is code. ImplicitUsings ends with "section .text" on every OS; before
; 2026-11-23 it did on Linux only, and NASM's COFF output wrote sl_mul, a label defined before
; any section directive, as an undefined symbol in the win-x64 library.

; int32_t sl_mul(int32_t a, int32_t b): a * b.
HIDDEN_FUNCTION sl_mul, 2
    mov     eax, ARG1d
    imul    eax, ARG2d
    RETURN
ENDFUNCTION
