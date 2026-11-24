; s.win.asm - StructErrors (drift), win-x64's variant: shape holds a 64-bit field, so the two RIDs
; disagree (ASM0133 from the outer build).

STRUCT shape, uint64_t a
