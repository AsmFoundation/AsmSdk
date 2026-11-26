// =============================================================================
// crc32c.wasm.s - libcrc32c's WebAssembly half (Crc32C sample): browser-wasm
// and wasi-wasm. crc32c.asm is the x64 half, crc32c.s the ARM64 half; all three
// export the same five functions (crc32c.map, the ExportsFile), with the same
// checksum convention (zlib's: 0 starts, a previous result continues;
// "123456789" gives 0xE3069283).
//
//     uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);
//     int      crc32c_implementation(void);
//     uint32_t crc32c_append_portable(uint32_t crc, const void *data, size_t length);
//     uint32_t crc32c_append_hardware(uint32_t crc, const void *data, size_t length);
//     uint32_t crc32c_append_hardware_3way(uint32_t crc, const void *data, size_t length);
//
// WebAssembly has neither a CRC instruction nor a carry-less multiply (SIMD128
// and relaxed SIMD included), so there is one real path here, a table one, and
// it is the fast table method rather than the x64 fallback's byte at a time:
// slicing-by-8, eight 256-entry tables that advance the checksum eight bytes
// per step (Intel's method; each step is eight independent lookups, which the
// engine can overlap, where one table makes a chain of eight dependent ones).
// crc32c_append and crc32c_append_portable both run it, and
// crc32c_implementation says CRC32C_PORTABLE (1): no hardware path exists.
// The two hardware exports trap (unreachable), as on x64 a hardware path
// faults on a CPU without the instructions; Crc32C.Net never calls them here
// (Crc32C.IsSupported is false for both on WebAssembly).
//
// The tables are computed on the first call, into zero-filled memory: crc32c.asm
// builds its table at assembly time with NASM's preprocessor, and WebAssembly
// has no assembler constants at all (asmsdk-wasm.inc's footguns), so nothing can
// be computed before run time here. 2,048 values once: about 10 us in Node 22's
// V8 (measured, the median over 2,000 fresh instances of one compiled module).
// One thread: a WebAssembly module's memory is its own unless the host shares
// it, and .NET's browser runtime is single-threaded by default.
// =============================================================================

BSS
.p2align 4
tables:                 // T0..T7, 256 x 4 bytes each: T0 at +0, T1 at +1024, ... T7 at +7168
    .skip 8192
ENDDATA tables
.p2align 2
tables_ready:           // 1 once crc32c_init has run
    .skip 4
ENDDATA tables_ready

// -----------------------------------------------------------------------------
// crc32c_init(): fills the eight tables.
//   T0[n]: the CRC-32C of the byte n, eight shifts of the reflected polynomial
//          0x82F63B78 (Castagnoli's 0x1EDC6F41, bit-reversed)
//   Tj[n]: (Tj-1[n] >> 8) ^ T0[Tj-1[n] & 0xff], the byte n followed by j zeros
// -----------------------------------------------------------------------------
LOCAL_FUNCTION crc32c_init, () -> ()
    .local i32, i32, i32, i32   // 0: n, 1: the value, 2: the bit count, 3: the address
    i32.const 0
    local.set 0
    loop                        // T0
        local.get 0
        local.set 1
        i32.const 8
        local.set 2
        loop                    // c = (c >> 1) ^ (poly & -(c & 1))
            local.get 1
            i32.const 1
            i32.shr_u
            i32.const 0x82F63B78    // llvm-mc wraps it to the i32 -2097792136: the same bytes
            i32.const 0
            local.get 1
            i32.const 1
            i32.and
            i32.sub                 // all ones when the low bit is set
            i32.and
            i32.xor
            local.set 1
            local.get 2
            i32.const 1
            i32.sub
            local.tee 2
            br_if 0
        end_loop
        ADDRESS tables
        local.get 0
        i32.const 2
        i32.shl
        i32.add
        local.get 1
        i32.store 0
        local.get 0
        i32.const 1
        i32.add
        local.tee 0
        i32.const 256
        i32.lt_u
        br_if 0
    end_loop

    // T1..T7 in one sweep: each entry from the one 1 KiB before it.
    ADDRESS tables
    i32.const 1024
    i32.add
    local.set 3
    loop
        local.get 3
        local.get 3
        i32.const 1024
        i32.sub
        i32.load 0              // Tj-1[n]
        local.tee 1
        i32.const 8
        i32.shr_u
        ADDRESS tables
        local.get 1
        i32.const 255
        i32.and
        i32.const 2
        i32.shl
        i32.add
        i32.load 0              // T0[Tj-1[n] & 0xff]
        i32.xor
        i32.store 0
        local.get 3
        i32.const 4
        i32.add
        local.tee 3
        ADDRESS tables
        i32.const 8192
        i32.add
        i32.lt_u
        br_if 0
    end_loop
    ADDRESS tables_ready
    i32.const 1
    i32.store 0
ENDFUNCTION crc32c_init

// -----------------------------------------------------------------------------
// crc32c_append_portable(crc, data, length) - slicing-by-8, then one byte per
// step for the 0-7 bytes left.
// @param crc     0 for a new checksum, or a previous result to continue
// @param data    the bytes (anything when the length is 0)
// @param length  their count (size_t: 32-bit on wasm32)
// @returns the checksum
// The running value is the complement of the checksum, as zlib's convention
// has it: complemented on entry and on exit.
// -----------------------------------------------------------------------------
PROTO uint32_t crc32c_append_portable(uint32_t crc, const uint8_t* data, size_t length)
FUNCTION crc32c_append_portable, (i32, i32, i32) -> (i32)
    .local i32, i32             // 3: the first word ^ crc, 4: the second word
    ADDRESS tables_ready
    i32.load 0
    i32.eqz
    if
        CALL crc32c_init
    end_if
    local.get 0
    i32.const -1
    i32.xor
    local.set 0

    block
    loop
        local.get 2
        i32.const 8
        i32.lt_u
        br_if 1
        // Two little-endian words (WebAssembly loads tolerate any alignment).
        local.get 1
        i32.load 0
        local.get 0
        i32.xor
        local.set 3
        local.get 1
        i32.load 4
        local.set 4
        // crc = T7[one & 255] ^ T6[(one >> 8) & 255] ^ T5[(one >> 16) & 255] ^ T4[one >> 24]
        //     ^ T3[two & 255] ^ T2[(two >> 8) & 255] ^ T1[(two >> 16) & 255] ^ T0[two >> 24]
        // Each index is scaled by 4 in one step ((x >> 6) & 1020 is ((x >> 8) & 255) * 4),
        // and each table's distance from T0 is the load's offset immediate.
        ADDRESS tables
        local.get 3
        i32.const 255
        i32.and
        i32.const 2
        i32.shl
        i32.add
        i32.load 7168           // T7
        ADDRESS tables
        local.get 3
        i32.const 6
        i32.shr_u
        i32.const 1020
        i32.and
        i32.add
        i32.load 6144           // T6
        i32.xor
        ADDRESS tables
        local.get 3
        i32.const 14
        i32.shr_u
        i32.const 1020
        i32.and
        i32.add
        i32.load 5120           // T5
        i32.xor
        ADDRESS tables
        local.get 3
        i32.const 24
        i32.shr_u
        i32.const 2
        i32.shl
        i32.add
        i32.load 4096           // T4
        i32.xor
        ADDRESS tables
        local.get 4
        i32.const 255
        i32.and
        i32.const 2
        i32.shl
        i32.add
        i32.load 3072           // T3
        i32.xor
        ADDRESS tables
        local.get 4
        i32.const 6
        i32.shr_u
        i32.const 1020
        i32.and
        i32.add
        i32.load 2048           // T2
        i32.xor
        ADDRESS tables
        local.get 4
        i32.const 14
        i32.shr_u
        i32.const 1020
        i32.and
        i32.add
        i32.load 1024           // T1
        i32.xor
        ADDRESS tables
        local.get 4
        i32.const 24
        i32.shr_u
        i32.const 2
        i32.shl
        i32.add
        i32.load 0              // T0
        i32.xor
        local.set 0
        local.get 1
        i32.const 8
        i32.add
        local.set 1
        local.get 2
        i32.const 8
        i32.sub
        local.set 2
        br 0
    end_loop
    end_block

    block                       // the 0-7 bytes left: crc = T0[(crc ^ byte) & 255] ^ (crc >> 8)
    loop
        local.get 2
        i32.eqz
        br_if 1
        ADDRESS tables
        local.get 0
        local.get 1
        i32.load8_u 0
        i32.xor
        i32.const 255
        i32.and
        i32.const 2
        i32.shl
        i32.add
        i32.load 0
        local.get 0
        i32.const 8
        i32.shr_u
        i32.xor
        local.set 0
        local.get 1
        i32.const 1
        i32.add
        local.set 1
        local.get 2
        i32.const 1
        i32.sub
        local.set 2
        br 0
    end_loop
    end_block
    local.get 0
    i32.const -1
    i32.xor
ENDFUNCTION crc32c_append_portable

// -----------------------------------------------------------------------------
// crc32c_append(crc, data, length) - the checksum on the fastest path, which
// on WebAssembly is the table one: no choice to make, so no dispatch either.
// -----------------------------------------------------------------------------
PROTO uint32_t crc32c_append(uint32_t crc, const uint8_t* data, size_t length)
FUNCTION crc32c_append, (i32, i32, i32) -> (i32)
    local.get 0
    local.get 1
    local.get 2
    TAILCALL crc32c_append_portable
ENDFUNCTION crc32c_append

// -----------------------------------------------------------------------------
// crc32c_implementation() - which path crc32c_append runs: CRC32C_PORTABLE (1),
// always, on WebAssembly.
// -----------------------------------------------------------------------------
PROTO int32_t crc32c_implementation(void)
FUNCTION crc32c_implementation, () -> (i32)
    i32.const 1                 // CRC32C_PORTABLE
ENDFUNCTION crc32c_implementation

// -----------------------------------------------------------------------------
// The hardware paths: no CRC instruction exists to run, so they trap, as a
// hardware path faults on an x64 or ARM64 CPU without the instructions.
// Exported all the same: crc32c.map names them for every RID, and a caller that
// checks crc32c_implementation (or Crc32C.IsSupported) never reaches them.
// -----------------------------------------------------------------------------
PROTO uint32_t crc32c_append_hardware(uint32_t crc, const uint8_t* data, size_t length)
FUNCTION crc32c_append_hardware, (i32, i32, i32) -> (i32)
    unreachable
ENDFUNCTION crc32c_append_hardware

PROTO uint32_t crc32c_append_hardware_3way(uint32_t crc, const uint8_t* data, size_t length)
FUNCTION crc32c_append_hardware_3way, (i32, i32, i32) -> (i32)
    unreachable
ENDFUNCTION crc32c_append_hardware_3way
