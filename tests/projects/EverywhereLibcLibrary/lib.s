// The ARM64 RIDs: the same two functions as lib.asm.
.include "asmsdk-gnu.inc"

RODATA
text:   .asciz "assembly"

FUNCTION elc_len
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ADDRESS x0, text
    CALL    strlen
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION elc_len

// x19 holds the block across the calls (callee-saved on every ARM64 OS).
FUNCTION elc_heap
    stp     x29, x30, [sp, #-32]!
    mov     x29, sp
    str     x19, [sp, #16]
    mov     x0, #64
    CALL    malloc
    cbz     x0, 1f
    mov     x19, x0
    mov     w1, #0x5a
    mov     x2, #64
    CALL    memset
    ldrb    w0, [x19, #63]
    cmp     w0, #0x5a
    b.ne    1f
    mov     x0, x19
    CALL    free
    mov     w0, #1
    b       2f
1:  mov     w0, #0
2:  ldr     x19, [sp, #16]
    ldp     x29, x30, [sp], #32
    ret
ENDFUNCTION elc_heap
