// pair.s - StructRaw's ARM64 RID, ImplicitUsings off: the layer included by hand; the STRUCT
// constants arrive through the wrapper the SDK writes for this source because of its STRUCT line.
.include "asmsdk-gnu.inc"
.text

STRUCT pair, uint8_t tag, uint64_t value

.if pair.value != 8 || pair_size != 16
    .error "the SDK laid out pair differently from C"
.endif

// uint64_t pair_sum(const pair* p): tag + value.
PROTO uint64_t pair_sum(const pair* p)
FUNCTION pair_sum
    ldrb    w1, [x0, #pair.tag]
    ldr     x2, [x0, #pair.value]
    add     x0, x2, x1
    ret
ENDFUNCTION pair_sum
