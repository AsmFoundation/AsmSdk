// =============================================================================
// qamath.s - the ARM64 half of QaMath (win-arm64, linux-arm64, osx-arm64), the
// same exports as qamath.asm. AAPCS64 is one convention on the three OSes:
// integers in x0-x7 and floats in v0-v7, counted apart; a struct of up to 16
// bytes comes back in x0:x1; arguments past the eighth go on the stack.
// =============================================================================

.include "asmsdk-gnu.inc"

RODATA
.ifdef WINDOWS
version:    .asciz "qamath 1.0 win-arm64"
.endif
.ifdef LINUX
version:    .asciz "qamath 1.0 linux-arm64"
.endif
.ifdef MACOS
version:    .asciz "qamath 1.0 osx-arm64"
.endif

    .data
    .p2align 3
counter:    .quad 0

// int32_t qa_add(int32_t a, int32_t b)
FUNCTION qa_add
    add     w0, w0, w1
    ret
ENDFUNCTION qa_add

// int64_t qa_sum_i64(const int64_t *values, size_t count)
FUNCTION qa_sum_i64
    mov     x2, #0
    cbz     x1, 2f
1:  ldr     x3, [x0], #8
    add     x2, x2, x3
    subs    x1, x1, #1
    b.ne    1b
2:  mov     x0, x2
    ret
ENDFUNCTION qa_sum_i64

// double qa_mix(int64_t a, double b, int64_t c, double d): a in x0, b in d0,
// c in x1, d in d1.
FUNCTION qa_mix
    scvtf   d2, x0
    fmul    d2, d2, d0
    scvtf   d3, x1
    fmul    d3, d3, d1
    fadd    d0, d2, d3
    ret
ENDFUNCTION qa_mix

// int64_t qa_many(int64_t a1, ..., int64_t a8): all eight in x0-x7.
FUNCTION qa_many
    mov     x8, #2
    madd    x0, x1, x8, x0
    mov     x8, #3
    madd    x0, x2, x8, x0
    mov     x8, #4
    madd    x0, x3, x8, x0
    mov     x8, #5
    madd    x0, x4, x8, x0
    mov     x8, #6
    madd    x0, x5, x8, x0
    mov     x8, #7
    madd    x0, x6, x8, x0
    mov     x8, #8
    madd    x0, x7, x8, x0
    ret
ENDFUNCTION qa_many

// int64_t qa_dot3(const vec3i *a, const vec3i *b)
FUNCTION qa_dot3
    ldpsw   x2, x3, [x0]
    ldrsw   x4, [x0, #8]
    ldpsw   x5, x6, [x1]
    ldrsw   x7, [x1, #8]
    mul     x0, x2, x5
    madd    x0, x3, x6, x0
    madd    x0, x4, x7, x0
    ret
ENDFUNCTION qa_dot3

// int64_t qa_pair_sum(pair p): the 8-byte struct arrives in x0.
FUNCTION qa_pair_sum
    asr     x1, x0, #32
    sxtw    x0, w0
    add     x0, x0, x1
    ret
ENDFUNCTION qa_pair_sum

// pair qa_make_pair(int32_t a, int32_t b): returned in x0.
FUNCTION qa_make_pair
    mov     w0, w0                      // zero-extends a
    orr     x0, x0, x1, lsl #32
    ret
ENDFUNCTION qa_make_pair

// quad qa_make_quad(int64_t x, int64_t y): a 16-byte struct comes back in
// x0:x1, which already hold x and y.
FUNCTION qa_make_quad
    ret
ENDFUNCTION qa_make_quad

// int64_t qa_reduce(const int32_t *values, size_t count, op): x19-x22 hold the
// state across the calls into C# (callee-saved).
FUNCTION qa_reduce
    stp     x29, x30, [sp, #-48]!
    mov     x29, sp
    stp     x19, x20, [sp, #16]
    stp     x21, x22, [sp, #32]
    mov     x19, x0
    mov     x20, x1
    mov     x21, x2
    mov     x22, #0
    cbz     x20, 2f
1:  mov     x0, x22
    ldr     w1, [x19], #4
    blr     x21
    mov     x22, x0
    subs    x20, x20, #1
    b.ne    1b
2:  mov     x0, x22
    ldp     x21, x22, [sp, #32]
    ldp     x19, x20, [sp, #16]
    ldp     x29, x30, [sp], #48
    ret
ENDFUNCTION qa_reduce

// const char *qa_version(void)
FUNCTION qa_version
    ADDRESS x0, version
    ret
ENDFUNCTION qa_version

// int64_t qa_utf8_count(const char *s)
FUNCTION qa_utf8_count
    mov     x1, #0
1:  ldrb    w2, [x0], #1
    cbz     w2, 2f
    and     w2, w2, #0xC0
    cmp     w2, #0x80
    cinc    x1, x1, ne
    b       1b
2:  mov     x0, x1
    ret
ENDFUNCTION qa_utf8_count

// size_t qa_upper_ascii(char *s, size_t length)
FUNCTION qa_upper_ascii
    mov     x2, #0
    cbz     x1, 3f
1:  ldrb    w3, [x0]
    sub     w4, w3, #97                 // 'a'
    cmp     w4, #25
    b.hi    2f
    sub     w3, w3, #32
    strb    w3, [x0]
    add     x2, x2, #1
2:  add     x0, x0, #1
    subs    x1, x1, #1
    b.ne    1b
3:  mov     x0, x2
    ret
ENDFUNCTION qa_upper_ascii

// int32_t qa_divmod(int32_t a, int32_t b, int32_t *quotient, int32_t *remainder)
FUNCTION qa_divmod
    cbz     w1, 1f
    sdiv    w4, w0, w1
    msub    w5, w4, w1, w0
    str     w4, [x2]
    str     w5, [x3]
    mov     w0, #0
    ret
1:  mov     w0, #-1
    ret
ENDFUNCTION qa_divmod

// bool qa_is_even(int64_t x)
FUNCTION qa_is_even
    tst     x0, #1
    cset    w0, eq
    ret
ENDFUNCTION qa_is_even

// int64_t qa_counter_add(int64_t delta): ARMv8.0 has no single-instruction
// atomic add (that is LSE, ARMv8.1), so a load-exclusive/store-exclusive loop.
FUNCTION qa_counter_add
    ADDRESS x1, counter
1:  ldaxr   x2, [x1]
    add     x2, x2, x0
    stlxr   w3, x2, [x1]
    cbnz    w3, 1b
    mov     x0, x2
    ret
ENDFUNCTION qa_counter_add

// void qa_add_f32(const float *a, const float *b, float *sum, size_t count)
// NEON: 4 floats per step, then the rest one at a time.
FUNCTION qa_add_f32
    and     x5, x3, #0xfffffffffffffffc
1:  cbz     x5, 2f
    ldr     q0, [x0], #16
    ldr     q1, [x1], #16
    fadd    v0.4s, v0.4s, v1.4s
    str     q0, [x2], #16
    sub     x5, x5, #4
    b       1b
2:  and     x3, x3, #3
3:  cbz     x3, 4f
    ldr     s0, [x0], #4
    ldr     s1, [x1], #4
    fadd    s0, s0, s1
    str     s0, [x2], #4
    sub     x3, x3, #1
    b       3b
4:  ret
ENDFUNCTION qa_add_f32

// int64_t qa_sum_squares(const int64_t *values, size_t count): through the
// static library's hidden qa_helper_square.
FUNCTION qa_sum_squares
    stp     x29, x30, [sp, #-48]!
    mov     x29, sp
    stp     x19, x20, [sp, #16]
    str     x21, [sp, #32]
    mov     x19, x0
    mov     x20, x1
    mov     x21, #0
    cbz     x20, 2f
1:  ldr     x0, [x19], #8
    CALL    qa_helper_square
    add     x21, x21, x0
    subs    x20, x20, #1
    b.ne    1b
2:  mov     x0, x21
    ldr     x21, [sp, #32]
    ldp     x19, x20, [sp, #16]
    ldp     x29, x30, [sp], #48
    ret
ENDFUNCTION qa_sum_squares

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
