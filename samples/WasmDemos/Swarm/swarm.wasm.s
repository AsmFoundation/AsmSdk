// =============================================================================
// swarm.wasm.s - the "Particle swarm" Wasm Demo: 120,000 particles (or 240,000
// or 500,000, the page's choice) pulled towards the pointer, in WebAssembly
// written by hand, drawing itself into an RGBA frame.
//
// The particles are stored as four arrays (x, y and the two velocities, f32),
// so one v128 load brings the same field of four particles and every step of
// the physics is one f32x4 instruction for four of them:
//     d = attractor - p;  f = k / (|d|^2 + 900);  v = (v + d * f) * 0.993;  p += v
// with a bounce off the frame's edges done by lane masks (f32x4.lt, f32x4.ge)
// and v128.bitselect, not by branches. Each particle then adds to a density
// image (one f32 per pixel) that fades by 0.78 a frame, four pixels per
// f32x4.mul, and the density, capped at 255, picks the pixel's colour from a
// 256-entry table that runs from the background through the theme's inks.
// A particle adds 18 * sqrt(120,000 / count): 18 at 120,000, 12.7 at 240,000,
// 8.8 at 500,000, so a larger swarm draws denser (its total grows as the
// square root of the count) without its core saturating into solid ink, the
// owner's pick of 2026-11-26 between keeping the brightness and adding 18 each.
//
// Without a pointer over the frame the attractor follows a slow figure of
// eight, cos(0.011 t) across and sin(0.022 t) down: WebAssembly has no sine or
// cosine instruction, so sin_poly below is one, by range reduction and a
// polynomial. The main button held down pushes the swarm away instead.
//
// Exports (demo.map): demo_init, demo_theme, demo_pointer (x < 0: the pointer
// left the frame), demo_wheel (unused), demo_command (1: scatter the swarm
// again, 3: pause or resume, and a multiple of 4 from 4 to 500,000: that many
// particles from now on) and demo_frame, which returns the particles moved
// (the count, or 0 when paused) for the page's figures.
// =============================================================================

BSS
.p2align 4
pos_x:                  // x of each particle (f32), room for 500,000; pos_y, vel_x and
    .skip 2000000       // vel_y follow, each 2,000,000 bytes after the one before, which
ENDDATA pos_x           // the loops' load and store offsets rely on
pos_y:
    .skip 2000000
ENDDATA pos_y
vel_x:
    .skip 2000000
ENDDATA vel_x
vel_y:
    .skip 2000000
ENDDATA vel_y
.p2align 4
density:                // one f32 per pixel, up to 1280 x 720
    .skip 3686400
ENDDATA density
.p2align 4
frame:                  // RGBA, row by row, up to 1280 x 720
    .skip 3686400
ENDDATA frame
.p2align 4
colours:                // 256 RGBA words: density 0 (the background) to 255 (the hottest ink)
    .skip 1024
ENDDATA colours
.p2align 4
state:                  // +0 width, +4 height (i32), +8 x and +12 y of the pointer (f32; x < 0:
    .skip 64            // none), +16 its buttons, +20 the frame count, +24 the random state,
ENDDATA state           // +28 paused, +32 dark, +36 the particle count, +40..+52 the four
                        // colour stops, +56 the density a particle adds (f32)

// -----------------------------------------------------------------------------
// sin_poly(x) -> sin x, to about 2e-4: x is brought into [-pi, pi] (minus the
// nearest multiple of 2 pi), folded into [-pi/2, pi/2] (sin(pi - x) = sin x),
// then x - x^3/3! + x^5/5! - x^7/7!, by Horner's rule on x^2.
// Local 1: x^2.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION sin_poly, (f32) -> (f32)
    .local f32
    local.get 0
    local.get 0
    f32.const 0x1.45f306p-3      // 1 / (2 pi)
    f32.mul
    f32.nearest
    f32.const 0x1.921fb6p2       // 2 pi
    f32.mul
    f32.sub
    local.set 0
    local.get 0
    f32.const 0x1.921fb6p0       // pi / 2
    f32.gt
    if
        f32.const 0x1.921fb6p1       // pi
        local.get 0
        f32.sub
        local.set 0
    end_if
    local.get 0
    f32.const -0x1.921fb6p0      // -pi / 2
    f32.lt
    if
        f32.const -0x1.921fb6p1      // -pi
        local.get 0
        f32.sub
        local.set 0
    end_if
    local.get 0
    local.get 0
    f32.mul
    local.set 1
    local.get 0
    f32.const 1.0
    local.get 1
    f32.const -0x1.555556p-3     // -1/3!
    local.get 1
    f32.const 0x1.111112p-7      // 1/5!
    local.get 1
    f32.const -0x1.a01a02p-13    // -1/7!
    f32.mul
    f32.add
    f32.mul
    f32.add
    f32.mul
    f32.add
    f32.mul
ENDFUNCTION sin_poly

// rng_next() -> a 32-bit pseudo-random number (xorshift32), from state+24.
LOCAL_FUNCTION rng_next, () -> (i32)
    .local i32
    ADDRESS state
    i32.load 24
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
    i32.store 24
    local.get 0
ENDFUNCTION rng_next

// rand_signed() -> a pseudo-random f32 in [-1, 1): the top 24 bits, scaled.
LOCAL_FUNCTION rand_signed, () -> (f32)
    CALL rng_next
    i32.const 8
    i32.shr_u
    f32.convert_i32_u
    f32.const 0x1p-23            // 2 / 2^24
    f32.mul
    f32.const 1.0
    f32.sub
ENDFUNCTION rand_signed

// -----------------------------------------------------------------------------
// scatter_range(first, end): the particles from byte offset first up to end
// (four bytes a particle; nothing when end is not past first), each at a
// random point of a disc around the frame's centre (radius 0.42 of the frame's
// smaller side, the middle 7% left empty), moving round it at 1.4 pixels a
// frame. Points are drawn in the square and kept when inside the ring: no
// trigonometry needed. The random state carries on from call to call, so the
// particles a larger count adds differ from the ones already there.
// Locals: 0 the particle's byte offset (from first), 1 end, 2 u, 3 v,
// 4 u^2 + v^2, 5 the radius, 6 the length of (u, v).
// -----------------------------------------------------------------------------
LOCAL_FUNCTION scatter_range, (i32, i32) -> ()
    .local f32, f32, f32, f32, f32
    // The loop below runs at least once, so an empty range must not reach it: it
    // would write one particle past the end (for a smaller count, into the ones
    // that count drops, which is harmless; at the capacity, into pos_y).
    local.get 0
    local.get 1
    i32.ge_u
    if
        return
    end_if
    ADDRESS state
    i32.load 0
    ADDRESS state
    i32.load 4
    i32.lt_s
    if f32                      // the smaller side
        ADDRESS state
        i32.load 0
        f32.convert_i32_s
    else
        ADDRESS state
        i32.load 4
        f32.convert_i32_s
    end_if
    f32.const 0x1.ae147ap-2      // 0.42
    f32.mul
    local.set 5
    loop                        // each particle
        loop                    // until (u, v) falls inside the ring
            CALL rand_signed
            local.tee 2
            local.get 2
            f32.mul
            CALL rand_signed
            local.tee 3
            local.get 3
            f32.mul
            f32.add
            local.tee 4
            f32.const 1.0
            f32.ge
            local.get 4
            f32.const 0x1.41205cp-8      // 0.0049: the middle 7% (0.07^2) stays empty
            f32.le
            i32.or
            br_if 0
        end_loop
        local.get 4
        f32.sqrt
        local.set 6
        ADDRESS pos_x
        local.get 0
        i32.add
        ADDRESS state
        i32.load 0
        f32.convert_i32_s
        f32.const 0.5
        f32.mul
        local.get 2
        local.get 5
        f32.mul
        f32.add
        f32.store 0             // x
        ADDRESS pos_x
        local.get 0
        i32.add
        ADDRESS state
        i32.load 4
        f32.convert_i32_s
        f32.const 0.5
        f32.mul
        local.get 3
        local.get 5
        f32.mul
        f32.add
        f32.store 2000000       // y
        ADDRESS pos_x
        local.get 0
        i32.add
        local.get 3
        f32.neg
        local.get 6
        f32.div
        f32.const 0x1.666666p0       // 1.4
        f32.mul
        f32.store 4000000       // vx: along the tangent (-v, u)
        ADDRESS pos_x
        local.get 0
        i32.add
        local.get 2
        local.get 6
        f32.div
        f32.const 0x1.666666p0       // 1.4
        f32.mul
        f32.store 6000000       // vy
        local.get 0
        i32.const 4
        i32.add
        local.tee 0
        local.get 1
        i32.lt_u
        br_if 0
    end_loop
ENDFUNCTION scatter_range

// scatter(): the whole swarm (the current count) anew, and an empty density
// image, so the old swarm leaves no trail.
LOCAL_FUNCTION scatter, () -> ()
    i32.const 0
    ADDRESS state
    i32.load 36
    i32.const 2
    i32.shl
    CALL scatter_range
    ADDRESS density
    i32.const 0
    i32.const 3686400
    memory.fill 0
ENDFUNCTION scatter

// set_count(n): n particles from now on (a multiple of 4 from 4 to 500,000:
// demo_command checks). A larger count scatters only the particles it adds, on
// the ring, so the swarm on screen carries on and the newcomers stream into
// it; a smaller one leaves the particles it drops in memory, unmoved and
// undrawn. Each particle's share of the density becomes
// 18 * sqrt(120,000 / n), in this order of f32 operations (check.mjs repeats
// it): exactly 18 at 120,000, so the default looks as it always did.
LOCAL_FUNCTION set_count, (i32) -> ()
    ADDRESS state
    i32.load 36
    i32.const 2
    i32.shl                     // from the first particle the old count lacks...
    local.get 0
    i32.const 2
    i32.shl                     // ...to the new count's end (nothing when n is smaller)
    CALL scatter_range
    ADDRESS state
    local.get 0
    i32.store 36
    ADDRESS state
    f32.const 120000.0
    local.get 0
    f32.convert_i32_s
    f32.div
    f32.sqrt
    f32.const 18.0
    f32.mul
    f32.store 56
ENDFUNCTION set_count

// -----------------------------------------------------------------------------
// build_colours(dark): the 256 colours, in three equal runs between four stops,
// each channel mixed in integers: (a * (255 - f) + b * f) / 255.
//   light: white, mint #3eea8c, teal #06706d, ink #062f2e
//   dark:  #15191c, teal #06706d, mint #3eea8c, #f2f5f4
// Locals: 1 i, 2 the run (0..2), 3 f, 4 the colour, 5 the channel's shift,
// 6 the run's first stop, 7 its second.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION build_colours, (i32) -> ()
    .local i32, i32, i32, i32, i32, i32, i32
    local.get 0
    if
        ADDRESS state
        i32.const 0xFF1C1915
        i32.store 40
        ADDRESS state
        i32.const 0xFF6D7006
        i32.store 44
        ADDRESS state
        i32.const 0xFF8CEA3E
        i32.store 48
        ADDRESS state
        i32.const 0xFFF4F5F2
        i32.store 52
    else
        ADDRESS state
        i32.const 0xFFFFFFFF
        i32.store 40
        ADDRESS state
        i32.const 0xFF8CEA3E
        i32.store 44
        ADDRESS state
        i32.const 0xFF6D7006
        i32.store 48
        ADDRESS state
        i32.const 0xFF2E2F06
        i32.store 52
    end_if
    i32.const 0
    local.set 1
    loop                        // i = 0..255
        local.get 1
        i32.const 3
        i32.mul
        local.tee 3
        i32.const 255
        i32.div_u
        local.tee 2
        i32.const 2
        i32.gt_u
        if
            i32.const 2         // i = 255 lands on 3: the last run's end
            local.set 2
        end_if
        local.get 3
        local.get 2
        i32.const 255
        i32.mul
        i32.sub
        local.set 3             // f, 0..255 within the run
        ADDRESS state
        local.get 2
        i32.const 2
        i32.shl
        i32.add
        local.tee 6
        i32.load 44
        local.set 7
        local.get 6
        i32.load 40
        local.set 6
        i32.const 0xFF000000
        local.set 4
        i32.const 0
        local.set 5
        loop                    // the channels at shifts 0, 8 and 16
            local.get 4
            local.get 6
            local.get 5
            i32.shr_u
            i32.const 255
            i32.and
            i32.const 255
            local.get 3
            i32.sub
            i32.mul
            local.get 7
            local.get 5
            i32.shr_u
            i32.const 255
            i32.and
            local.get 3
            i32.mul
            i32.add
            i32.const 255
            i32.div_u
            local.get 5
            i32.shl
            i32.or
            local.set 4
            local.get 5
            i32.const 8
            i32.add
            local.tee 5
            i32.const 24
            i32.lt_u
            br_if 0
        end_loop
        ADDRESS colours
        local.get 1
        i32.const 2
        i32.shl
        i32.add
        local.get 4
        i32.store 0
        local.get 1
        i32.const 1
        i32.add
        local.tee 1
        i32.const 256
        i32.lt_u
        br_if 0
    end_loop
ENDFUNCTION build_colours

// -----------------------------------------------------------------------------
// demo_init(width, height) -> the frame's address, or 0 when the frame would be
// larger than 1280 x 720, empty, or not a multiple of 4 pixels wide (the
// density loops go four pixels at a time). Scatters a swarm of 120,000, the
// count a page starts with (a second call starts over at 120,000 too).
// -----------------------------------------------------------------------------
FUNCTION demo_init, (i32, i32) -> (i32)
    local.get 0
    i32.const 4
    i32.lt_s
    local.get 1
    i32.const 1
    i32.lt_s
    i32.or
    local.get 0
    i32.const 3
    i32.and
    i32.or
    local.get 0
    local.get 1
    i32.mul
    i32.const 921600
    i32.gt_u
    i32.or
    if
        i32.const 0
        return
    end_if
    ADDRESS state
    local.get 0
    i32.store 0
    ADDRESS state
    local.get 1
    i32.store 4
    ADDRESS state
    f32.const -1.0
    f32.store 8                 // no pointer yet
    ADDRESS state
    i32.const 0
    i32.store 16
    ADDRESS state
    i32.const 0
    i32.store 20
    ADDRESS state
    i32.const 0x2545F491
    i32.store 24
    ADDRESS state
    i32.const 0
    i32.store 28
    ADDRESS state
    i32.const 120000
    i32.store 36
    ADDRESS state
    f32.const 18.0              // 18 * sqrt(120,000 / 120,000), as set_count would make it
    f32.store 56
    CALL scatter
    i32.const 0
    CALL build_colours
    ADDRESS frame
ENDFUNCTION demo_init

// demo_theme(dark): the colours, light (0) or dark (1).
FUNCTION demo_theme, (i32) -> ()
    ADDRESS state
    local.get 0
    i32.store 32
    local.get 0
    CALL build_colours
ENDFUNCTION demo_theme

// demo_pointer(x, y, buttons): where the swarm goes (x < 0: the pointer left), and
// whether it is pushed away (the main button).
FUNCTION demo_pointer, (f32, f32, i32) -> ()
    ADDRESS state
    local.get 0
    f32.store 8
    ADDRESS state
    local.get 1
    f32.store 12
    ADDRESS state
    local.get 2
    i32.store 16
ENDFUNCTION demo_pointer

// demo_wheel(x, y, delta): nothing to zoom here.
FUNCTION demo_wheel, (f32, f32, f32) -> ()
ENDFUNCTION demo_wheel

// demo_command(command): 1 scatters the swarm again, 3 pauses or resumes it, and
// a multiple of 4 from 4 to 500,000 makes that the particle count (the page's
// choices send 120000, 240000 and 500000). Any other command is ignored: a
// count past the arrays' room, or one the four-wide physics loop cannot step.
FUNCTION demo_command, (i32) -> ()
    local.get 0
    i32.const 1
    i32.eq
    if
        CALL scatter
    end_if
    local.get 0
    i32.const 3
    i32.eq
    if
        ADDRESS state
        ADDRESS state
        i32.load 28
        i32.const 1
        i32.xor
        i32.store 28
    end_if
    local.get 0
    i32.const 4
    i32.ge_s
    local.get 0
    i32.const 500000
    i32.le_s
    i32.and
    local.get 0
    i32.const 3
    i32.and
    i32.eqz
    i32.and
    if
        local.get 0
        CALL set_count
    end_if
ENDFUNCTION demo_command

// -----------------------------------------------------------------------------
// demo_frame() -> the particles moved: one step of the swarm (unless paused),
// the density's fade and splat, and the frame's colours.
// Locals: 0 the byte offset, 1 the end, 2 a pixel's address; v128 3 the
// attractor's x, 4 its y, 5 k, 6 0.993, 7 900, 8 zero, 9 width - 1,
// 10 height - 1, 11 width, 12 height, 13 x, 14 y, 15 vx, 16 vy, 17 dx, 18 dy,
// 19 f; f32 20 the attractor's x, 21 its y (then a particle's density);
// i32 22 the frame count, 23 width.
// -----------------------------------------------------------------------------
FUNCTION demo_frame, () -> (i32)
    .local i32, i32, i32, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, f32, f32, i32, i32
    ADDRESS state
    i32.load 0
    local.set 23
    ADDRESS state
    i32.load 28
    i32.eqz
    if
        ADDRESS state
        ADDRESS state
        i32.load 20
        i32.const 1
        i32.add
        local.tee 22
        i32.store 20
        // The attractor: the pointer, or the figure of eight.
        ADDRESS state
        f32.load 8
        f32.const 0.0
        f32.ge
        if
            ADDRESS state
            f32.load 8
            local.set 20
            ADDRESS state
            f32.load 12
            local.set 21
        else
            local.get 23
            f32.convert_i32_s
            f32.const 0.5
            f32.mul
            local.get 22
            f32.convert_i32_s
            f32.const 0x1.6872bp-7       // 0.011
            f32.mul
            f32.const 0x1.921fb6p0       // cos t = sin(t + pi / 2)
            f32.add
            CALL sin_poly
            local.get 23
            f32.convert_i32_s
            f32.const 0x1.333334p-2      // 0.3
            f32.mul
            f32.mul
            f32.add
            local.set 20
            ADDRESS state
            i32.load 4
            f32.convert_i32_s
            f32.const 0.5
            f32.mul
            local.get 22
            f32.convert_i32_s
            f32.const 0x1.6872bp-6       // 0.022
            f32.mul
            CALL sin_poly
            ADDRESS state
            i32.load 4
            f32.convert_i32_s
            f32.const 0x1.1eb852p-2      // 0.28
            f32.mul
            f32.mul
            f32.add
            local.set 21
        end_if
        local.get 20
        f32x4.splat
        local.set 3
        local.get 21
        f32x4.splat
        local.set 4
        f32.const 60.0              // pulled; pushed (-90) while the main button is down
        local.set 20
        ADDRESS state
        i32.load 16
        i32.const 1
        i32.and
        if
            f32.const -90.0
            local.set 20
        end_if
        local.get 20
        f32x4.splat
        local.set 5
        f32.const 0x1.fc6a7ep-1      // 0.993
        f32x4.splat
        local.set 6
        f32.const 900.0
        f32x4.splat
        local.set 7
        f32.const 0.0
        f32x4.splat
        local.set 8
        local.get 23
        f32.convert_i32_s
        local.tee 20
        f32.const 1.0
        f32.sub
        f32x4.splat
        local.set 9
        local.get 20
        f32x4.splat
        local.set 11
        ADDRESS state
        i32.load 4
        f32.convert_i32_s
        local.tee 20
        f32.const 1.0
        f32.sub
        f32x4.splat
        local.set 10
        local.get 20
        f32x4.splat
        local.set 12

        // The physics, four particles per instruction, over the current count
        // (a multiple of 4, so the loop ends on a whole vector).
        ADDRESS pos_x
        local.set 0
        ADDRESS pos_x
        ADDRESS state
        i32.load 36
        i32.const 2
        i32.shl
        i32.add
        local.set 1
        loop
            local.get 3
            local.get 0
            v128.load 0
            local.tee 13
            f32x4.sub
            local.set 17            // dx
            local.get 4
            local.get 0
            v128.load 2000000
            local.tee 14
            f32x4.sub
            local.set 18            // dy
            local.get 5
            local.get 17
            local.get 17
            f32x4.mul
            local.get 18
            local.get 18
            f32x4.mul
            f32x4.add
            local.get 7
            f32x4.add
            f32x4.div
            local.set 19            // f = k / (dx^2 + dy^2 + 900)
            local.get 0
            v128.load 4000000
            local.get 17
            local.get 19
            f32x4.mul
            f32x4.add
            local.get 6
            f32x4.mul
            local.set 15            // vx = (vx + dx f) 0.993
            local.get 0
            v128.load 6000000
            local.get 18
            local.get 19
            f32x4.mul
            f32x4.add
            local.get 6
            f32x4.mul
            local.set 16            // vy
            local.get 13
            local.get 15
            f32x4.add
            local.set 13            // x += vx
            local.get 14
            local.get 16
            f32x4.add
            local.set 14            // y += vy
            // Off the left or right edge: the velocity turns back, the position is clamped.
            local.get 15
            f32x4.neg
            local.get 15
            local.get 13
            local.get 8
            f32x4.lt
            local.get 13
            local.get 11
            f32x4.ge
            v128.or
            v128.bitselect
            local.set 15
            local.get 16
            f32x4.neg
            local.get 16
            local.get 14
            local.get 8
            f32x4.lt
            local.get 14
            local.get 12
            f32x4.ge
            v128.or
            v128.bitselect
            local.set 16
            local.get 0
            local.get 13
            local.get 8
            f32x4.max
            local.get 9
            f32x4.min
            v128.store 0
            local.get 0
            local.get 14
            local.get 8
            f32x4.max
            local.get 10
            f32x4.min
            v128.store 2000000
            local.get 0
            local.get 15
            v128.store 4000000
            local.get 0
            local.get 16
            v128.store 6000000
            local.get 0
            i32.const 16
            i32.add
            local.tee 0
            local.get 1
            i32.lt_u
            br_if 0
        end_loop

        // The density fades, four pixels at a time...
        f32.const 0x1.8f5c28p-1      // 0.78
        f32x4.splat
        local.set 17
        ADDRESS density
        local.tee 0
        local.get 23
        ADDRESS state
        i32.load 4
        i32.mul
        i32.const 2
        i32.shl
        i32.add
        local.set 1
        loop
            local.get 0
            local.get 0
            v128.load 0
            local.get 17
            f32x4.mul
            v128.store 0
            local.get 0
            i32.const 16
            i32.add
            local.tee 0
            local.get 1
            i32.lt_u
            br_if 0
        end_loop
        // ...and each particle adds its share (18 at 120,000) to its pixel.
        ADDRESS state
        f32.load 56
        local.set 21
        ADDRESS pos_x
        local.set 0
        ADDRESS pos_x
        ADDRESS state
        i32.load 36
        i32.const 2
        i32.shl
        i32.add
        local.set 1
        loop
            ADDRESS density
            local.get 0
            f32.load 2000000        // y
            i32.trunc_sat_f32_s
            local.get 23
            i32.mul
            local.get 0
            f32.load 0              // x
            i32.trunc_sat_f32_s
            i32.add
            i32.const 2
            i32.shl
            i32.add
            local.tee 2
            local.get 2
            f32.load 0
            local.get 21
            f32.add
            f32.store 0
            local.get 0
            i32.const 4
            i32.add
            local.tee 0
            local.get 1
            i32.lt_u
            br_if 0
        end_loop
    end_if

    // The colours: density 0..255 picks one of the 256.
    ADDRESS density
    local.set 0
    ADDRESS frame
    local.set 2
    ADDRESS density
    local.get 23
    ADDRESS state
    i32.load 4
    i32.mul
    i32.const 2
    i32.shl
    i32.add
    local.set 1
    loop
        local.get 2
        ADDRESS colours
        local.get 0
        f32.load 0
        f32.const 255.0
        f32.min
        i32.trunc_sat_f32_u
        i32.const 2
        i32.shl
        i32.add
        i32.load 0
        i32.store 0
        local.get 2
        i32.const 4
        i32.add
        local.set 2
        local.get 0
        i32.const 4
        i32.add
        local.tee 0
        local.get 1
        i32.lt_u
        br_if 0
    end_loop
    ADDRESS state
    i32.load 28
    if i32
        i32.const 0
    else
        ADDRESS state
        i32.load 36             // the count, which every step moved
    end_if
ENDFUNCTION demo_frame
