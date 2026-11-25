// =============================================================================
// life.wasm.s - the "Life, 16 million cells" Wasm Demo: Conway's Life on a
// 4096 x 4096 board that wraps around at its edges (a torus), in WebAssembly
// written by hand, drawing itself into an RGBA frame.
//
// The board is 4096 rows of 512 bytes, one bit per cell: cell x of row y is
// bit (x & 7) of byte y * 512 + x / 8, so a v128 load brings 128 neighbouring
// cells of one row. A generation is computed 128 cells at a time without a
// branch per cell, by bit-slicing: for each of the 128 lanes, the eight
// neighbours are eight bit-vectors (the rows above and below, each shifted one
// cell west and east, and the row itself shifted both ways), and full adders
// made of xor, and and or add them as binary numbers whose bits are whole
// vectors. Life's rule, "born with 3 neighbours, survives with 2 or 3", is then
// (count == 2 or 3) and (count is odd or the cell is alive): two more and/or
// steps on the count's bit-planes. About 35 instructions per 128 cells.
//
// The sweep goes down one 128-cell column at a time, keeping the partial sums
// of the row above and the middle row in locals, so each row is loaded and
// shifted once per column, not three times.
//
// Everything the page shows is drawn here: demo_frame steps the board and
// paints the visible part into the frame (one cell per 1, 2, 4 or 8 pixels
// when zoomed in, and when zoomed out the share of live cells in each 2 x 2,
// 4 x 4 or 8 x 8 block, as a shade), with the cells that died in this
// generation in the trail colour. The pointer pans (the main button) and draws
// cells (the secondary one), the wheel zooms at the pointer. The page
// (docs/demos/demo.js) only starts the module, forwards the pointer and copies
// the frame to its canvas.
//
// Exports (demo.map): demo_init, demo_theme, demo_pointer, demo_wheel,
// demo_command (1: a new random board, 2: clear it, 3: pause or resume) and
// demo_frame, which returns the cells it stepped (16,777,216, or 0 when
// paused) for the page's cells-per-second figure.
// =============================================================================

BSS
.p2align 4
board_a:                // the board, or the next one: 4096 rows x 512 bytes
    .skip 2097152
ENDDATA board_a
.p2align 4
board_b:
    .skip 2097152
ENDDATA board_b
.p2align 4
frame:                  // RGBA, row by row: the largest frame demo_init accepts is 1280 x 720
    .skip 3686400
ENDDATA frame
.p2align 4
palette:                // 0..64: the share of live cells, background to ink; 65: the trail
    .skip 272
ENDDATA palette
.p2align 4
state:                  // +0 the current board, +4 width, +8 height, +12 zoom (pixels per cell
    .skip 64            // = 2^zoom, -3..3), +16 x and +20 y of the view's centre (cells, f32),
ENDDATA state           // +24 x and +28 y of the pointer last seen (f32), +32 its buttons,
                        // +36 paused, +40 the random generator's state, +44 dark

// LIFE_ROW_SUMS: sums the row at the address in local 6, for the 128-cell column
// at byte offset local 2 (local 3: the column to its west, local 4: to its east).
// Leaves in local 8 the row itself (V), in 11/12 the sum (bit, carry) of its
// west and east neighbours, and in 13/14 the sum of all three, as bit-planes.
//   W = V shifted one cell east, taking the lowest cell of the column west of it
//   E = V shifted one cell west, taking the highest cell of the column east of it
// (i64x2 shifts move bits within each 64-bit lane; the shuffle brings the lane
// next door, so that its edge bit can be shifted in.)
.macro LIFE_ROW_SUMS
    local.get 6
    local.get 2
    i32.add
    v128.load 0
    local.set 8             // V: 128 cells of the row
    local.get 8
    i32.const 1
    i64x2.shl               // each cell takes the value of its west neighbour...
    local.get 6
    local.get 3
    i32.add
    v128.load 0             // the column to the west
    local.get 8
    i8x16.shuffle 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23
    i32.const 63
    i64x2.shr_u             // ...and the lowest cell of each lane, its west neighbour's high bit
    v128.or
    local.set 9             // W
    local.get 8
    i32.const 1
    i64x2.shr_u             // each cell takes the value of its east neighbour...
    local.get 8
    local.get 6
    local.get 4
    i32.add
    v128.load 0             // the column to the east
    i8x16.shuffle 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23
    i32.const 63
    i64x2.shl               // ...and the highest cell, its east neighbour's low bit
    v128.or
    local.set 10            // E
    local.get 9
    local.get 10
    v128.xor
    local.set 11            // s2 = W ^ E
    local.get 9
    local.get 10
    v128.and
    local.set 12            // c2 = W & E
    local.get 11
    local.get 8
    v128.xor
    local.set 13            // s3 = s2 ^ V
    local.get 12
    local.get 11
    local.get 8
    v128.and
    v128.or
    local.set 14            // c3 = c2 | (s2 & V)
.endm

// -----------------------------------------------------------------------------
// step_board(src, dst): one generation of the board at src into the board at
// dst. Locals: 2 the column's byte offset, 3 the column to its west, 4 to its
// east, 5 the row, 6 a row's address; 8..14 the row just summed (the row
// below), 15/16 the row above's three-cell sum, 17..21 the middle row (V, s2,
// c2, s3, c3), 22..25 scratch.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION step_board, (i32, i32) -> ()
    .local i32, i32, i32, i32, i32, i32, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128
    i32.const 0
    local.set 2
    loop                    // each column of 128 cells: offsets 0, 16, ..., 496
        local.get 2
        i32.const 496
        i32.add
        i32.const 496
        i32.and
        local.set 3         // the column to the west, wrapping to 496
        local.get 2
        i32.const 16
        i32.add
        i32.const 496
        i32.and
        local.set 4         // the column to the east, wrapping to 0

        local.get 0
        i32.const 2096640   // row 4095, the row above row 0
        i32.add
        local.set 6
        LIFE_ROW_SUMS
        local.get 13
        local.set 15
        local.get 14
        local.set 16
        local.get 0         // row 0, the first middle row
        local.set 6
        LIFE_ROW_SUMS
        local.get 8
        local.set 17
        local.get 11
        local.set 18
        local.get 12
        local.set 19
        local.get 13
        local.set 20
        local.get 14
        local.set 21

        i32.const 0
        local.set 5
        loop                // each row y: the row below is (y + 1) & 4095
            local.get 5
            i32.const 1
            i32.add
            i32.const 4095
            i32.and
            i32.const 9
            i32.shl
            local.get 0
            i32.add
            local.set 6
            LIFE_ROW_SUMS

            // The neighbour count of the middle row, as bit-planes: the ones are a
            // full adder of the three single sums (above's three cells, the middle's
            // two, below's three); the twos, one of the three carries plus the ones'
            // carry; anything more is four or more, and kills.
            local.get 15
            local.get 18
            v128.xor
            local.tee 22        // aS3 ^ mS2
            local.get 13
            v128.xor
            local.set 25        // ones
            local.get 15
            local.get 18
            v128.and
            local.get 22
            local.get 13
            v128.and
            v128.or
            local.set 24        // the ones' carry
            local.get 16
            local.get 19
            v128.xor
            local.tee 23        // aC3 ^ mC2
            local.get 14
            v128.xor
            local.set 22        // the carries' sum bit
            local.get 16
            local.get 19
            v128.and
            local.get 23
            local.get 14
            v128.and
            v128.or
            local.set 23        // the carries' carry: a four

            local.get 1         // the address of the next generation's cells
            local.get 5
            i32.const 9
            i32.shl
            i32.add
            local.get 2
            i32.add
            local.get 22
            local.get 24
            v128.xor            // twos = the carries' sum bit ^ the ones' carry
            local.get 23
            local.get 22
            local.get 24
            v128.and
            v128.or             // fours = a four among the carries, or both twos
            v128.andnot         // a count of 2 or 3...
            local.get 25
            local.get 17
            v128.or             // ...with 3 (odd) or a live cell
            v128.and
            v128.store 0

            local.get 20        // the window moves down: the middle row is now above,
            local.set 15
            local.get 21
            local.set 16
            local.get 8         // and the row below is now the middle
            local.set 17
            local.get 11
            local.set 18
            local.get 12
            local.set 19
            local.get 13
            local.set 20
            local.get 14
            local.set 21
            local.get 5
            i32.const 1
            i32.add
            local.tee 5
            i32.const 4096
            i32.lt_u
            br_if 0
        end_loop
        local.get 2
        i32.const 16
        i32.add
        local.tee 2
        i32.const 512
        i32.lt_u
        br_if 0
    end_loop
ENDFUNCTION step_board

// -----------------------------------------------------------------------------
// rng_next() -> a 32-bit pseudo-random number (xorshift32, Marsaglia), from the
// state at state+40, which must not be 0.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION rng_next, () -> (i32)
    .local i32
    ADDRESS state
    i32.load 40
    local.tee 0
    i32.const 13
    i32.shl
    local.get 0
    i32.xor
    local.tee 0
    i32.const 17
    i32.shr_u
    local.get 0
    i32.xor
    local.tee 0
    i32.const 5
    i32.shl
    local.get 0
    i32.xor
    local.set 0
    ADDRESS state
    local.get 0
    i32.store 40
    local.get 0
ENDFUNCTION rng_next

// -----------------------------------------------------------------------------
// fill_soup(board): a random board, each cell alive with probability 3/8 (one
// random word AND the OR of two more), the usual density of a Life "soup".
// -----------------------------------------------------------------------------
LOCAL_FUNCTION fill_soup, (i32) -> ()
    .local i32              // 1: the end of the board
    local.get 0
    i32.const 2097152
    i32.add
    local.set 1
    loop
        local.get 0
        CALL rng_next
        CALL rng_next
        CALL rng_next
        i32.or
        i32.and
        i32.store 0
        local.get 0
        i32.const 4
        i32.add
        local.tee 0
        local.get 1
        i32.lt_u
        br_if 0
    end_loop
ENDFUNCTION fill_soup

// -----------------------------------------------------------------------------
// build_palette(dark): palette[0..64], the share of live cells in a block from
// none (the background) to all (the ink), and palette[65], the trail. The
// colours are the AsmFoundation ones, as RGBA bytes (little-endian words):
//   light: background white, ink #062f2e, trail mint #3eea8c
//   dark:  background #15191c, ink mint #3eea8c, trail teal #06706d
// Each channel is mixed in integers: (bg * (64 - d) + ink * d) / 64.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION build_palette, (i32) -> ()
    .local i32, i32, i32, i32, i32, i32     // 1: background, 2: ink, 3: trail, 4: d, 5: channel shift, 6: colour
    local.get 0
    if
        i32.const 0xFF1C1915
        local.set 1
        i32.const 0xFF8CEA3E
        local.set 2
        i32.const 0xFF6D7006
        local.set 3
    else
        i32.const 0xFFFFFFFF
        local.set 1
        i32.const 0xFF2E2F06
        local.set 2
        i32.const 0xFF8CEA3E
        local.set 3
    end_if
    i32.const 0
    local.set 4
    loop                    // d = 0..64
        i32.const 0xFF000000
        local.set 6
        i32.const 0
        local.set 5
        loop                // the channels at shifts 0 (red), 8 (green) and 16 (blue)
            local.get 6
            local.get 1
            local.get 5
            i32.shr_u
            i32.const 255
            i32.and
            i32.const 64
            local.get 4
            i32.sub
            i32.mul
            local.get 2
            local.get 5
            i32.shr_u
            i32.const 255
            i32.and
            local.get 4
            i32.mul
            i32.add
            i32.const 6
            i32.shr_u
            local.get 5
            i32.shl
            i32.or
            local.set 6
            local.get 5
            i32.const 8
            i32.add
            local.tee 5
            i32.const 24
            i32.lt_u
            br_if 0
        end_loop
        ADDRESS palette
        local.get 4
        i32.const 2
        i32.shl
        i32.add
        local.get 6
        i32.store 0
        local.get 4
        i32.const 1
        i32.add
        local.tee 4
        i32.const 65
        i32.lt_u
        br_if 0
    end_loop
    ADDRESS palette
    local.get 3
    i32.store 260           // palette[65]
ENDFUNCTION build_palette

// -----------------------------------------------------------------------------
// pixels_per_cell(zoom) -> 2^zoom as f32 (zoom -3..3: 1/8 to 8).
// -----------------------------------------------------------------------------
LOCAL_FUNCTION pixels_per_cell, (i32) -> (f32)
    local.get 0
    i32.const 0
    i32.ge_s
    if f32
        i32.const 1
        local.get 0
        i32.shl
        f32.convert_i32_s
    else
        f32.const 1.0
        i32.const 1
        i32.const 0
        local.get 0
        i32.sub
        i32.shl
        f32.convert_i32_s
        f32.div
    end_if
ENDFUNCTION pixels_per_cell

// -----------------------------------------------------------------------------
// wrap_cells(value) -> value modulo 4096, in [0, 4096): the board's coordinates
// wrap around, and the view's centre is kept inside one copy of it.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION wrap_cells, (f32) -> (f32)
    local.get 0
    local.get 0
    f32.const 4096.0
    f32.div
    f32.floor
    f32.const 4096.0
    f32.mul
    f32.sub
ENDFUNCTION wrap_cells

// -----------------------------------------------------------------------------
// render(): paints the view into the frame. The view's left edge in cells is
// cx - (width / 2) / pixels_per_cell, kept as a fixed-point number with 8
// fraction bits (cell x of a pixel = (fx >> 8) & 4095: the shift is
// arithmetic, so a negative coordinate wraps to the board's far side too).
// Locals: 0 width, 1 height, 2 zoom, 3 fx0, 4 fy0, 5 the current board, 6 the
// previous one, 7 pixel x, 8 pixel y, 9 the output address, 10 fx, 11 the
// row's address in the current board, 12 in the previous board, 13 x,
// 14 the step in fixed point (zoomed in) or the block size s (zoomed out),
// 15 the count of live cells, 16 r, 17 the row of a block, 18 mask, 19 the
// palette scale (log2 of 64 / s^2).
// -----------------------------------------------------------------------------
LOCAL_FUNCTION render, () -> ()
    .local i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32
    ADDRESS state
    i32.load 4
    local.set 0
    ADDRESS state
    i32.load 8
    local.set 1
    ADDRESS state
    i32.load 12
    local.set 2
    ADDRESS state
    i32.load 0
    local.tee 5
    ADDRESS board_a
    i32.eq
    if i32         // the previous generation is the other board
        ADDRESS board_b
    else
        ADDRESS board_a
    end_if
    local.set 6
    // fx0 = (cx - (width / 2) / ppc) * 256, fy0 likewise
    ADDRESS state
    f32.load 16
    local.get 0
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    local.get 2
    CALL pixels_per_cell
    f32.div
    f32.sub
    f32.const 256.0
    f32.mul
    f32.floor
    i32.trunc_sat_f32_s
    local.set 3
    ADDRESS state
    f32.load 20
    local.get 1
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    local.get 2
    CALL pixels_per_cell
    f32.div
    f32.sub
    f32.const 256.0
    f32.mul
    f32.floor
    i32.trunc_sat_f32_s
    local.set 4
    ADDRESS frame
    local.set 9

    local.get 2
    i32.const 0
    i32.ge_s
    if
        // Zoomed in: a pixel shows one cell; the fixed-point step is 256 / 2^zoom.
        i32.const 256
        local.get 2
        i32.shr_u
        local.set 14
        i32.const 0
        local.set 8
        loop                // each pixel row
            local.get 4
            local.get 8
            local.get 14
            i32.mul
            i32.add
            i32.const 8
            i32.shr_s
            i32.const 4095
            i32.and
            i32.const 9
            i32.shl
            local.tee 11
            local.get 6
            i32.add
            local.set 12
            local.get 11
            local.get 5
            i32.add
            local.set 11
            local.get 3
            local.set 10
            i32.const 0
            local.set 7
            loop            // each pixel: the colour of its cell
                // palette[alive * 64 + (was alive and is not) * 65], without a branch
                // (llvm-mc 23's type checker refuses an untyped select)
                local.get 9
                ADDRESS palette
                local.get 11
                local.get 10
                i32.const 8
                i32.shr_s
                i32.const 4095
                i32.and
                local.tee 13
                i32.const 3
                i32.shr_u
                i32.add
                i32.load8_u 0
                local.get 13
                i32.const 7
                i32.and
                local.tee 15            // the cell's bit in its byte
                i32.shr_u
                i32.const 1
                i32.and
                local.tee 17            // alive now
                i32.const 6
                i32.shl
                local.get 12
                local.get 13
                i32.const 3
                i32.shr_u
                i32.add
                i32.load8_u 0
                local.get 15
                i32.shr_u               // bit 0: alive a generation ago
                local.get 17
                i32.const 1
                i32.xor
                i32.and
                i32.const 65
                i32.mul
                i32.add
                i32.const 2
                i32.shl
                i32.add
                i32.load 0
                i32.store 0
                local.get 9
                i32.const 4
                i32.add
                local.set 9
                local.get 10
                local.get 14
                i32.add
                local.set 10
                local.get 7
                i32.const 1
                i32.add
                local.tee 7
                local.get 0
                i32.lt_u
                br_if 0
            end_loop
            local.get 8
            i32.const 1
            i32.add
            local.tee 8
            local.get 1
            i32.lt_u
            br_if 0
        end_loop
    else
        // Zoomed out: a pixel shows an s x s block (s = 2^-zoom: 2, 4 or 8, aligned to s,
        // inside one byte of each row), shaded by its share of live cells:
        // palette[count * 64 / s^2].
        i32.const 1
        i32.const 0
        local.get 2
        i32.sub
        i32.shl
        local.set 14            // s
        i32.const 1
        local.get 14
        i32.shl
        i32.const 1
        i32.sub
        local.set 18            // mask: s low bits
        i32.const 6
        i32.const 0
        local.get 2
        i32.sub
        i32.const 1
        i32.shl
        i32.sub
        local.set 19            // log2(64 / s^2) = 6 - 2 * -zoom
        local.get 3
        i32.const 8
        i32.shr_s
        i32.const 0
        local.get 14
        i32.sub
        i32.and
        local.set 3             // the first block's x, aligned down to s
        local.get 4
        i32.const 8
        i32.shr_s
        i32.const 0
        local.get 14
        i32.sub
        i32.and
        local.set 4             // and its y
        i32.const 0
        local.set 8
        loop                    // each pixel row: blocks starting at row y = fy + py * s
            local.get 4
            local.get 8
            local.get 14
            i32.mul
            i32.add
            local.set 17
            local.get 3
            local.set 10
            i32.const 0
            local.set 7
            loop                // each pixel
                local.get 10
                i32.const 4095
                i32.and
                local.set 13    // x
                i32.const 0
                local.set 15
                i32.const 0
                local.set 16
                loop            // each of the block's s rows
                    local.get 15
                    local.get 5
                    local.get 17
                    local.get 16
                    i32.add
                    i32.const 4095
                    i32.and
                    i32.const 9
                    i32.shl
                    i32.add
                    local.get 13
                    i32.const 3
                    i32.shr_u
                    i32.add
                    i32.load8_u 0
                    local.get 13
                    i32.const 7
                    i32.and
                    i32.shr_u
                    local.get 18
                    i32.and
                    i32.popcnt
                    i32.add
                    local.set 15
                    local.get 16
                    i32.const 1
                    i32.add
                    local.tee 16
                    local.get 14
                    i32.lt_u
                    br_if 0
                end_loop
                local.get 9
                ADDRESS palette
                local.get 15
                local.get 19
                i32.shl
                i32.const 2
                i32.shl
                i32.add
                i32.load 0
                i32.store 0
                local.get 9
                i32.const 4
                i32.add
                local.set 9
                local.get 10
                local.get 14
                i32.add
                local.set 10
                local.get 7
                i32.const 1
                i32.add
                local.tee 7
                local.get 0
                i32.lt_u
                br_if 0
            end_loop
            local.get 8
            i32.const 1
            i32.add
            local.tee 8
            local.get 1
            i32.lt_u
            br_if 0
        end_loop
    end_if
ENDFUNCTION render

// -----------------------------------------------------------------------------
// paint(x, y): brings a 3 x 3 block of cells to life around the cell under the
// frame pixel (x, y), on the current board.
// Locals: 2 the cell's x, 3 its y, 4 dx, 5 dy, 6 a cell's x, 7 the byte's address.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION paint, (f32, f32) -> ()
    .local i32, i32, i32, i32, i32, i32
    ADDRESS state
    f32.load 16
    local.get 0
    ADDRESS state
    i32.load 4
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    f32.sub
    ADDRESS state
    i32.load 12
    CALL pixels_per_cell
    f32.div
    f32.add
    f32.floor
    i32.trunc_sat_f32_s
    local.set 2
    ADDRESS state
    f32.load 20
    local.get 1
    ADDRESS state
    i32.load 8
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    f32.sub
    ADDRESS state
    i32.load 12
    CALL pixels_per_cell
    f32.div
    f32.add
    f32.floor
    i32.trunc_sat_f32_s
    local.set 3
    i32.const -1
    local.set 5
    loop                    // dy = -1..1
        i32.const -1
        local.set 4
        loop                // dx = -1..1
            local.get 2
            local.get 4
            i32.add
            i32.const 4095
            i32.and
            local.set 6
            ADDRESS state
            i32.load 0
            local.get 3
            local.get 5
            i32.add
            i32.const 4095
            i32.and
            i32.const 9
            i32.shl
            i32.add
            local.get 6
            i32.const 3
            i32.shr_u
            i32.add
            local.tee 7
            local.get 7
            i32.load8_u 0
            i32.const 1
            local.get 6
            i32.const 7
            i32.and
            i32.shl
            i32.or
            i32.store8 0
            local.get 4
            i32.const 1
            i32.add
            local.tee 4
            i32.const 2
            i32.lt_s
            br_if 0
        end_loop
        local.get 5
        i32.const 1
        i32.add
        local.tee 5
        i32.const 2
        i32.lt_s
        br_if 0
    end_loop
ENDFUNCTION paint

// -----------------------------------------------------------------------------
// demo_init(width, height) -> the frame's address, or 0 when the frame would be
// larger than 1280 x 720 (or empty). Starts a random board, the view on its
// centre at one pixel per cell, and the light palette.
// -----------------------------------------------------------------------------
FUNCTION demo_init, (i32, i32) -> (i32)
    local.get 0
    i32.const 1
    i32.lt_s
    local.get 1
    i32.const 1
    i32.lt_s
    i32.or
    local.get 0
    local.get 1
    i32.mul
    i32.const 921600        // 1280 x 720 pixels
    i32.gt_u
    i32.or
    if
        i32.const 0
        return
    end_if
    ADDRESS state
    local.get 0
    i32.store 4
    ADDRESS state
    local.get 1
    i32.store 8
    ADDRESS state
    i32.const 0
    i32.store 12            // zoom 0: one pixel per cell
    ADDRESS state
    f32.const 2048.0
    f32.store 16
    ADDRESS state
    f32.const 2048.0
    f32.store 20
    ADDRESS state
    i32.const 0
    i32.store 32
    ADDRESS state
    i32.const 0
    i32.store 36
    ADDRESS state
    i32.const 0x2545F491    // any seed but 0
    i32.store 40
    ADDRESS state
    ADDRESS board_a
    i32.store 0
    ADDRESS board_a
    CALL fill_soup
    ADDRESS board_b
    i32.const 0
    i32.const 2097152
    memory.fill 0
    i32.const 0
    CALL build_palette
    ADDRESS frame
ENDFUNCTION demo_init

// demo_theme(dark): the palette, light (0) or dark (1).
FUNCTION demo_theme, (i32) -> ()
    ADDRESS state
    local.get 0
    i32.store 44
    local.get 0
    CALL build_palette
ENDFUNCTION demo_theme

// -----------------------------------------------------------------------------
// demo_pointer(x, y, buttons): with the main button held since the last call,
// the view follows the pointer (a pan); with the secondary one, cells are drawn
// where it is.
// -----------------------------------------------------------------------------
FUNCTION demo_pointer, (f32, f32, i32) -> ()
    local.get 2
    ADDRESS state
    i32.load 32
    i32.and
    i32.const 1
    i32.and
    if                      // a pan: the centre moves against the pointer, in cells
        ADDRESS state
        ADDRESS state
        f32.load 16
        local.get 0
        ADDRESS state
        f32.load 24
        f32.sub
        ADDRESS state
        i32.load 12
        CALL pixels_per_cell
        f32.div
        f32.sub
        CALL wrap_cells
        f32.store 16
        ADDRESS state
        ADDRESS state
        f32.load 20
        local.get 1
        ADDRESS state
        f32.load 28
        f32.sub
        ADDRESS state
        i32.load 12
        CALL pixels_per_cell
        f32.div
        f32.sub
        CALL wrap_cells
        f32.store 20
    end_if
    local.get 2
    i32.const 2
    i32.and
    if
        local.get 0
        local.get 1
        CALL paint
    end_if
    ADDRESS state
    local.get 0
    f32.store 24
    ADDRESS state
    local.get 1
    f32.store 28
    ADDRESS state
    local.get 2
    i32.store 32
ENDFUNCTION demo_pointer

// -----------------------------------------------------------------------------
// demo_wheel(x, y, delta): one zoom step at the pointer (delta < 0: in, > 0:
// out), from 8 cells... to 8 pixels per cell; the cell under the pointer stays
// under it. Locals: 3 the old pixels per cell, 4 the new zoom.
// -----------------------------------------------------------------------------
FUNCTION demo_wheel, (f32, f32, f32) -> ()
    .local f32, i32
    ADDRESS state
    i32.load 12
    local.tee 4
    CALL pixels_per_cell
    local.set 3
    local.get 4
    local.get 2
    f32.const 0.0
    f32.lt
    if i32                  // towards you: in
        i32.const 1
    else
        i32.const -1
    end_if
    i32.add
    local.tee 4
    i32.const -3
    i32.lt_s
    local.get 4
    i32.const 3
    i32.gt_s
    i32.or
    if
        return              // already at the end of the range
    end_if
    // centre' = centre + (p - size / 2) * (1 / old - 1 / new)
    ADDRESS state
    ADDRESS state
    f32.load 16
    local.get 0
    ADDRESS state
    i32.load 4
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    f32.sub
    f32.const 1.0
    local.get 3
    f32.div
    f32.const 1.0
    local.get 4
    CALL pixels_per_cell
    f32.div
    f32.sub
    f32.mul
    f32.add
    CALL wrap_cells
    f32.store 16
    ADDRESS state
    ADDRESS state
    f32.load 20
    local.get 1
    ADDRESS state
    i32.load 8
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    f32.sub
    f32.const 1.0
    local.get 3
    f32.div
    f32.const 1.0
    local.get 4
    CALL pixels_per_cell
    f32.div
    f32.sub
    f32.mul
    f32.add
    CALL wrap_cells
    f32.store 20
    ADDRESS state
    local.get 4
    i32.store 12
ENDFUNCTION demo_wheel

// -----------------------------------------------------------------------------
// demo_command(command): 1 a new random board, 2 an empty one (draw on it), 3
// pause or resume. Both boards change on 1 and 2, so no trail of the old board
// is left.
// -----------------------------------------------------------------------------
FUNCTION demo_command, (i32) -> ()
    local.get 0
    i32.const 3
    i32.eq
    if
        ADDRESS state
        ADDRESS state
        i32.load 36
        i32.const 1
        i32.xor
        i32.store 36
        return
    end_if
    ADDRESS board_a
    i32.const 0
    i32.const 4194304       // both boards: they are next to each other
    memory.fill 0
    ADDRESS state
    ADDRESS board_a
    i32.store 0
    local.get 0
    i32.const 1
    i32.eq
    if
        ADDRESS board_a
        CALL fill_soup
    end_if
ENDFUNCTION demo_command

// -----------------------------------------------------------------------------
// demo_frame() -> the cells stepped: one generation (unless paused), then the
// frame. The new generation goes to the other board, which becomes current.
// -----------------------------------------------------------------------------
FUNCTION demo_frame, () -> (i32)
    .local i32, i32         // 0: the current board, 1: the work done
    i32.const 0
    local.set 1
    ADDRESS state
    i32.load 36
    i32.eqz
    if
        ADDRESS state
        i32.load 0
        local.tee 0
        local.get 0
        ADDRESS board_a
        i32.eq
        if i32
            ADDRESS board_b
        else
            ADDRESS board_a
        end_if
        local.tee 0
        CALL step_board
        ADDRESS state
        local.get 0
        i32.store 0
        i32.const 16777216
        local.set 1
    end_if
    CALL render
    local.get 1
ENDFUNCTION demo_frame
