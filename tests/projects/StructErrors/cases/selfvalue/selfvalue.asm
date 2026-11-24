; selfvalue.asm - StructErrors: line 4 must fail (ASM0130): a struct cannot hold itself (a
; pointer to it, node* next, would be fine).

STRUCT node, int32_t value, node inner
