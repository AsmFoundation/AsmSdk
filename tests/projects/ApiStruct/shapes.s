// shapes.s - ApiStruct's ARM64 RIDs: the functions of shapes.asm. The constants come from the
// SDK through this source's wrapper (llvm-mc has no pre-include option).
.include "shapes.s.inc"

// The layout, checked by the assembler: .if reads the constants.
.if rect_size != 20 || rect.max != 8 || rect.flags != 16 || point.y != 4
    .error "the SDK laid out rect differently from C"
.endif

// int32_t rect_area(const rect* r): (max.x - min.x) * (max.y - min.y).
PROTO int32_t rect_area(const rect* r)
FUNCTION rect_area
    ldr     w1, [x0, #(rect.max + point.x)]
    ldr     w2, [x0, #(rect.min + point.x)]
    sub     w1, w1, w2
    ldr     w3, [x0, #(rect.max + point.y)]
    ldr     w4, [x0, #(rect.min + point.y)]
    sub     w3, w3, w4
    mul     w0, w1, w3
    ret
ENDFUNCTION rect_area

// void point_set(point* p, int32_t x, int32_t y): writes both fields through the pointer.
PROTO void point_set(point* p, int32_t x, int32_t y)
FUNCTION point_set
    str     w1, [x0, #point.x]
    str     w2, [x0, #point.y]
    ret
ENDFUNCTION point_set
