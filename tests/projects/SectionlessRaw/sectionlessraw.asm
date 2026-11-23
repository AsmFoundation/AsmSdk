; sectionlessraw.asm - the x64 RIDs of SectionlessRaw: ImplicitUsings is disabled, so the
; source includes asmsdk.inc and says "default rel" itself, and names no section. HIDDEN_FUNCTION
; starts with __SECT__, "[section .text]" while no section was named, so raw_mul is defined in
; .text on win-x64 too.
%include "asmsdk.inc"
default rel

; int32_t raw_mul(int32_t a, int32_t b): a * b.
HIDDEN_FUNCTION raw_mul, 2
    mov     eax, ARG1d
    imul    eax, ARG2d
    RETURN
ENDFUNCTION
