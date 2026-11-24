// kinds.s - ApiStruct's ARM64 RIDs: the functions of kinds.asm, over the same STRUCT lines (a
// pointer field written "uint8_t* data", which GNU macros alone could not lay out).
.include "shapes.s.inc"

STRUCT hash_state, uint32_t h[8], uint64_t length, uint8_t block[64], bool final
STRUCT sorter, int32_t (*cmp)(int32_t, int32_t), void* context, sorter* next
STRUCT mixed, bool flag, const uint8_t* data, uint32_t count, uint16_t tail, char tag

.if hash_state_size != 112 || hash_state.final != 104 || mixed.tag != 22 || sorter.next != 16
    .error "the SDK laid out the kinds differently from C"
.endif

// uint64_t state_sum(const hash_state* s): h[0] + ... + h[7] + length + block[63] + final. A
// 32-bit load zeroes the register's upper half, so x2 holds each h[i] as a 64-bit value.
PROTO uint64_t state_sum(const hash_state* s)
FUNCTION state_sum
    mov     x1, #0
    .irp i, 0, 1, 2, 3, 4, 5, 6, 7
    ldr     w2, [x0, #(hash_state.h + 4 * \i)]
    add     x1, x1, x2
    .endr
    ldr     x2, [x0, #hash_state.length]
    add     x1, x1, x2
    ldrb    w2, [x0, #(hash_state.block + 63)]
    add     x1, x1, x2
    ldrb    w2, [x0, #hash_state.final]
    add     x0, x1, x2
    ret
ENDFUNCTION state_sum

// int32_t sorter_call(const sorter* s, int32_t a, int32_t b): s->cmp(a, b).
PROTO int32_t sorter_call(const sorter* s, int32_t a, int32_t b)
FUNCTION sorter_call
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ldr     x3, [x0, #sorter.cmp]
    mov     w0, w1
    mov     w1, w2
    blr     x3
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION sorter_call

// uint32_t mixed_get(const mixed* m): count + tail + data[0] + tag when flag is set, else 0.
PROTO uint32_t mixed_get(const mixed* m)
FUNCTION mixed_get
    ldrb    w1, [x0, #mixed.flag]
    cbz     w1, 1f
    ldr     w2, [x0, #mixed.count]
    ldrh    w3, [x0, #mixed.tail]
    add     w2, w2, w3
    ldr     x4, [x0, #mixed.data]
    ldrb    w3, [x4]
    add     w2, w2, w3
    ldrb    w3, [x0, #mixed.tag]
    add     w0, w2, w3
    ret
1:
    mov     w0, #0
    ret
ENDFUNCTION mixed_get

// size_t struct_sizes(size_t* out): every struct's size constant in out[0..4]; returns 5.
PROTO size_t struct_sizes(size_t* out)
FUNCTION struct_sizes
    mov     x1, #point_size
    str     x1, [x0, #0]
    mov     x1, #rect_size
    str     x1, [x0, #8]
    mov     x1, #hash_state_size
    str     x1, [x0, #16]
    mov     x1, #sorter_size
    str     x1, [x0, #24]
    mov     x1, #mixed_size
    str     x1, [x0, #32]
    mov     x0, #5
    ret
ENDFUNCTION struct_sizes
