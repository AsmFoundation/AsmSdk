// checksum for the ARM64 RIDs: win-arm64, linux-arm64, linux-musl-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

// uint64_t checksum_fnv1a(const uint8_t *data, size_t length)
// FNV-1a, 64-bit (see checksum.asm). The 64-bit constants are built 16 bits at a time with
// movz/movk: no literal pool, which every object format would place differently.
FUNCTION checksum_fnv1a
    movz    x2, #0x2325                 // offset basis 0xcbf29ce484222325
    movk    x2, #0x8422, lsl #16
    movk    x2, #0x9ce4, lsl #32
    movk    x2, #0xcbf2, lsl #48
    movz    x3, #0x01b3                 // prime 0x100000001b3
    movk    x3, #0x0100, lsl #32
    cbz     x1, 2f
1:  ldrb    w4, [x0], #1
    eor     x2, x2, x4
    mul     x2, x2, x3
    subs    x1, x1, #1
    b.ne    1b
2:  mov     x0, x2
    ret
ENDFUNCTION checksum_fnv1a

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
