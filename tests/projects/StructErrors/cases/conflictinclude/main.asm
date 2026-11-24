; main.asm - StructErrors: pair is declared here, then again, differently, by the include below,
; whose line 3 must fail (ASM0136) before anything is assembled.

STRUCT pair, uint32_t a, uint32_t b
%include "cases/conflictinclude/other.inc"
