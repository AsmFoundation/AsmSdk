; a.asm - StructErrors (conflict): pair as two 32-bit fields. Each source lays out only what it
; declares, so both assemble; the library then holds two pairs, which AsmReadApi reports at b.asm.

STRUCT pair, uint32_t a, uint32_t b
