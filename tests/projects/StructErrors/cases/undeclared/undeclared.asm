; undeclared.asm - StructErrors: line 4 must fail (ASM0130): point is declared after rect, which
; holds it by value (C needs it first, and so does the layout).

STRUCT rect, point min, point max
STRUCT point, int32_t x, int32_t y
