// =============================================================================
// crc32c.s - libcrc32c, the ARM64 half: win-arm64, linux-arm64, osx-arm64.
// crc32c.asm is the x64 half, with the same exports and the same algorithm;
// read it for the derivations, this file follows it step by step.
//
// Exports (crc32c.map), in AAPCS64, the C convention of all three OSes
// (w0 = crc, x1 = data, x2 = length; the checksum comes back in w0):
//     uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);
//     int      crc32c_implementation(void);
//     uint32_t crc32c_append_portable(uint32_t crc, const void *data, size_t length);
//     uint32_t crc32c_append_hardware(uint32_t crc, const void *data, size_t length);
//     uint32_t crc32c_append_hardware_3way(uint32_t crc, const void *data, size_t length);
//
// The paths:
//   portable       a 256-entry table, one byte per step. Any ARM64 CPU.
//   hardware       the CRC32 extension's crc32cx, 8 bytes per step, one chain.
//   hardware_3way  three crc32cx chains per block, merged with PMULL (the
//                  carry-less multiply of the AES extension), as the x64 file
//                  merges with PCLMULQDQ: crc32cx is the same function as x64's
//                  crc32 r64, and PMULL the same product as PCLMULQDQ, so the
//                  merge constants K = x^(8n - 33) mod P are the same numbers.
//
// Which path crc32c_append runs is picked on the first call:
//   Linux    the ID_AA64ISAR0_EL1 register (CRC32 and AES/PMULL fields); Linux
//            emulates user-space reads of the ID registers (HWCAP_CPUID, since
//            kernel 4.11)
//   Windows  kernel32's IsProcessorFeaturePresent: the one import of the
//            library, which the SDK links into every Windows output
//   macOS    the 3-way path: every Apple silicon CPU has both extensions, and
//            macOS does not let user code read the ID registers
//
// Nothing here needs a C library: the library is built freestanding.
// =============================================================================

.include "asmsdk-gnu.inc"

// llvm-mc assembles for the baseline ARMv8.0 unless told otherwise; the paths
// that use these are only reached on CPUs that have them.
    .arch_extension crc
    .arch_extension aes

// The CRC-32C polynomial 0x1EDC6F41, bit-reflected (crc32c.inc's CRC32C_POLY).
.set CRC32C_POLY, 0x82F63B78
// crc32c_implementation's answers (crc32c.inc).
.set CRC32C_PORTABLE, 1
.set CRC32C_HARDWARE, 2
.set CRC32C_HARDWARE_3WAY, 3
// Windows' processor feature numbers (winnt.h).
.set PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE, 30
.set PF_ARM_V8_CRC32_INSTRUCTIONS_AVAILABLE, 31

// -----------------------------------------------------------------------------
// X_POW n - sets CRC32C_XPOW to x^n mod P, bit-reflected (bit 31 holds x^0),
// at assembly time: one bitwise CRC step per power of x, exactly crc32c.asm's
// X_POW. llvm-mc evaluates the .rept loop and the .set expressions itself.
// -----------------------------------------------------------------------------
.macro X_POW n
    .set CRC32C_XPOW, 0x80000000
    .rept \n
        .set CRC32C_XPOW, (CRC32C_XPOW >> 1) ^ (CRC32C_POLY & -(CRC32C_XPOW & 1))
    .endr
.endm

// -----------------------------------------------------------------------------
// MOVE32 reg, value - a 32-bit constant into a W register, in two halves.
// -----------------------------------------------------------------------------
.macro MOVE32 reg, value
    movz    \reg, #((\value) & 0xFFFF)
    movk    \reg, #(((\value) >> 16) & 0xFFFF), lsl #16
.endm

// -----------------------------------------------------------------------------
// THREE_WAY lane - the 3-lane kernel over every whole block of 3 * lane bytes
// that is left (crc32c.asm's THREE_WAY):
//     crc = crc32cx(c2, last8 ^ clmul(c0, K_2lane) ^ clmul(c1, K_lane))
// lane: bytes per lane, a multiple of 8, at least 16, at most 1360 (the load
// offsets and the block size must fit the instructions' immediates).
// in/out  w0  the raw CRC register (not inverted)
// in/out  x1  the data; advanced past the blocks processed
// in/out  x2  bytes left; reduced by the blocks processed
// Clobbers w5, w8, w9, x10-x14, v1, v2, v6, v7 (all caller-saved in AAPCS64).
// -----------------------------------------------------------------------------
.macro THREE_WAY lane
    .if ((\lane) % 8) != 0 || (\lane) < 16
        .error "THREE_WAY: the lane must be a multiple of 8, at least 16"
    .endif
    X_POW (8 * 2 * (\lane) - 33)
    .set CRC32C_K_TWO_LANES, CRC32C_XPOW
    X_POW (8 * (\lane) - 33)
    .set CRC32C_K_ONE_LANE, CRC32C_XPOW

    cmp     x2, #(3 * (\lane))
    b.lo    3f
    MOVE32  w5, CRC32C_K_TWO_LANES
    fmov    d6, x5                          // c0's multiplier
    MOVE32  w5, CRC32C_K_ONE_LANE
    fmov    d7, x5                          // c1's multiplier
1:  mov     w8, #0                          // lane 1 starts from 0
    mov     w9, #0                          // lane 2 starts from 0
    mov     x10, #((\lane) / 8 - 1)         // all but the last 8 bytes of each lane
2:  ldr     x11, [x1]
    ldr     x12, [x1, #(\lane)]
    ldr     x13, [x1, #(2 * (\lane))]
    crc32cx w0, w0, x11                     // lane 0
    crc32cx w8, w8, x12                     // lane 1
    crc32cx w9, w9, x13                     // lane 2
    add     x1, x1, #8
    subs    x10, x10, #1
    b.ne    2b
    // x1 = block + lane - 8: the last 8 bytes of lane 0.
    ldr     x11, [x1]
    ldr     x12, [x1, #(\lane)]
    ldr     x13, [x1, #(2 * (\lane))]
    crc32cx w0, w0, x11                     // lane 0 complete: c0
    crc32cx w8, w8, x12                     // lane 1 complete: c1
    fmov    d1, x0                          // a W write cleared the upper half
    fmov    d2, x8
    pmull   v1.1q, v1.1d, v6.1d             // c0 * x^(8 * 2 lane - 33)
    pmull   v2.1q, v2.1d, v7.1d             // c1 * x^(8 * lane - 33)
    eor     v1.16b, v1.16b, v2.16b
    fmov    x14, d1                         // the product fits in 64 bits
    eor     x14, x14, x13                   // folded into lane 2's last 8 bytes
    crc32cx w0, w9, x14                     // lane 2 complete: the block's CRC
    add     x1, x1, #(2 * (\lane) + 8)      // to the next block
    sub     x2, x2, #(3 * (\lane))
    cmp     x2, #(3 * (\lane))
    b.hs    1b
3:
.endm

// ---- crc32c_append -------------------------------------------------------------
// One indirect branch through `dispatch` (x16, the intra-procedure scratch
// register): before the first call it points at `resolve`, afterwards at the
// chosen path, with the caller's arguments untouched in w0, x1, x2.
FUNCTION crc32c_append
    ADDRESS x16, dispatch
    ldr     x16, [x16]
    br      x16
ENDFUNCTION crc32c_append

// resolve - the first call's target: picks the path, then continues as that
// path with the caller's arguments and return address.
resolve:
    stp     x29, x30, [sp, #-48]!
    mov     x29, sp
    stp     x0, x1, [sp, #16]
    str     x2, [sp, #32]
    CALL    crc32c_select                   // x0 = the path
    mov     x16, x0
    ldp     x0, x1, [sp, #16]
    ldr     x2, [sp, #32]
    ldp     x29, x30, [sp], #48
    br      x16

// ---- crc32c_implementation -------------------------------------------------------
FUNCTION crc32c_implementation
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ADDRESS x9, dispatch
    ldr     x0, [x9]
    ADDRESS x10, resolve
    cmp     x0, x10
    b.ne    1f
    CALL    crc32c_select                   // not chosen yet: choose now
1:  mov     w1, #CRC32C_PORTABLE
    ADDRESS x10, impl_hardware
    cmp     x0, x10
    b.ne    2f
    mov     w1, #CRC32C_HARDWARE
2:  ADDRESS x10, impl_hardware_3way
    cmp     x0, x10
    b.ne    3f
    mov     w1, #CRC32C_HARDWARE_3WAY
3:  mov     w0, w1
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION crc32c_implementation

// crc32c_select - picks the fastest path this CPU runs and stores it in
// dispatch; returns it in x0. Threads that race here store the same value, and
// an aligned 8-byte store is single-copy atomic on ARM64, so a reader sees
// `resolve` (and selects again, harmlessly) or a complete path. x19 is
// callee-saved, so the choice survives the kernel32 calls on Windows.
LOCAL_FUNCTION crc32c_select
    stp     x29, x30, [sp, #-32]!
    mov     x29, sp
    str     x19, [sp, #16]
    ADDRESS x19, impl_portable
.ifdef LINUX
    mrs     x9, ID_AA64ISAR0_EL1
    ubfx    x10, x9, #16, #4                // CRC32 field: 1 = crc32 and crc32c
    cbz     x10, 1f
    ADDRESS x19, impl_hardware
    ubfx    x10, x9, #4, #4                 // AES field: 2 = AES and PMULL
    cmp     x10, #2
    b.lo    1f
    ADDRESS x19, impl_hardware_3way
1:
.endif
.ifdef WINDOWS
    mov     w0, #PF_ARM_V8_CRC32_INSTRUCTIONS_AVAILABLE
    CALL    IsProcessorFeaturePresent
    cbz     w0, 1f
    ADDRESS x19, impl_hardware
    mov     w0, #PF_ARM_V8_CRYPTO_INSTRUCTIONS_AVAILABLE
    CALL    IsProcessorFeaturePresent
    cbz     w0, 1f
    ADDRESS x19, impl_hardware_3way
1:
.endif
.ifdef MACOS
    ADDRESS x19, impl_hardware_3way
.endif
    ADDRESS x9, dispatch
    str     x19, [x9]
    mov     x0, x19
    ldr     x19, [sp, #16]
    ldp     x29, x30, [sp], #32
    ret
ENDFUNCTION crc32c_select

// ---- the portable path ---------------------------------------------------------------
FUNCTION crc32c_append_portable
    b       impl_portable
ENDFUNCTION crc32c_append_portable

impl_portable:
    mvn     w0, w0                          // the register runs inverted
    cbz     x2, 2f
    ADDRESS x3, crc32c_table
1:  ldrb    w4, [x1], #1
    eor     w5, w0, w4
    and     w5, w5, #0xFF                   // index = (register ^ byte) & 0xFF
    ldr     w5, [x3, w5, uxtw #2]
    eor     w0, w5, w0, lsr #8
    subs    x2, x2, #1
    b.ne    1b
2:  mvn     w0, w0
    ret

// ---- the one-chain path ----------------------------------------------------------------
FUNCTION crc32c_append_hardware
    b       impl_hardware
ENDFUNCTION crc32c_append_hardware

impl_hardware:
    mvn     w0, w0
// hardware_rest - the single-chain tail shared with the 3-way path.
// in: w0 = raw register, x1 = data, x2 = length. Returns the checksum.
hardware_rest:
    lsr     x3, x2, #3                      // whole quadwords
    cbz     x3, 2f
1:  ldr     x4, [x1], #8
    crc32cx w0, w0, x4
    subs    x3, x3, #1
    b.ne    1b
2:  and     x2, x2, #7                      // 0-7 bytes left
    cbz     x2, 4f
3:  ldrb    w4, [x1], #1
    crc32cb w0, w0, w4
    subs    x2, x2, #1
    b.ne    3b
4:  mvn     w0, w0
    ret

// ---- the 3-way path ----------------------------------------------------------------------
FUNCTION crc32c_append_hardware_3way
    b       impl_hardware_3way
ENDFUNCTION crc32c_append_hardware_3way

impl_hardware_3way:
    mvn     w0, w0
    THREE_WAY 512                           // 1,536-byte blocks
    THREE_WAY 64                            // then 192-byte blocks
    b       hardware_rest                   // then 8 bytes, then 1 byte at a time

// ---- data --------------------------------------------------------------------------------
RODATA
    .p2align 6
// crc32c_table[b] = the CRC register after one byte b from 0, built at
// assembly time with X_POW's step (the same table as crc32c.asm's).
crc32c_table:
    .set CRC32C_BYTE, 0
    .rept 256
        .set CRC32C_REG, CRC32C_BYTE
        .rept 8
            .set CRC32C_REG, (CRC32C_REG >> 1) ^ (CRC32C_POLY & -(CRC32C_REG & 1))
        .endr
        .word CRC32C_REG
        .set CRC32C_BYTE, CRC32C_BYTE + 1
    .endr

    .data
    .p2align 3
// Where crc32c_append branches: `resolve` until the first call has picked a
// path. A pointer in a writable section: the loader relocates it.
dispatch:
    .quad resolve
