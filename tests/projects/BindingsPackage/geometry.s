// geometry.s - BindingsPackage's ARM64 RIDs: the function of geometry.asm, over the same STRUCT
// line (the SDK compares the RIDs' structs as it compares their PROTO lines).

STRUCT point, int32_t x, int32_t y

.if point_size != 8 || point.y != 4
    .error "the SDK laid out point differently from C"
.endif

// int32_t point_sum(const point* p): p->x + p->y.
PROTO int32_t point_sum(const point* p)
FUNCTION point_sum
    ldr     w1, [x0, #point.x]
    ldr     w2, [x0, #point.y]
    add     w0, w1, w2
    ret
ENDFUNCTION point_sum
