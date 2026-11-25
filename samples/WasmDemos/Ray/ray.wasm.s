// =============================================================================
// ray.wasm.s - the "Ray-marched scene" Wasm Demo: three spheres and a checkered
// floor with soft shadows, drawn without the GPU by WebAssembly written by
// hand, four rays at a time.
//
// The scene is a distance field: for a point p, the distance to the nearest
// surface is the smallest of |p - c| - r for each sphere and p.y for the floor.
// A ray from the camera advances by that distance until it is closer than
// 0.001 t (a hit), or 40 units away (the sky), or 96 steps have passed. Each
// f32x4 holds the same quantity for four neighbouring rays; a ray that is done
// keeps its place through lane masks (f32x4.lt, v128.andnot, v128.bitselect)
// while the others go on, and the loop ends when every lane is done
// (i32x4.all_true). The surface's normal is exact (p - c) / r for a sphere, up
// for the floor; the light comes from (-0.5, 0.8, -0.35), and a second march
// towards it gives the soft shadow (the smallest 10 h / s along the way, Inigo
// Quilez's estimate). Colours are the AsmFoundation ones: teal, mint and amber
// spheres, a floor of two greys, a sky that brightens with height.
//
// The picture refines in four passes: one ray per 8 x 8, 4 x 4, 2 x 2 block,
// then per pixel, a share of about 16,000 rays per demo_frame, so the page
// stays responsive and the camera can move while it draws; any change starts
// over from the coarsest pass. Dragging with the main button orbits the camera
// (sin and cos are polynomials here: WebAssembly has no such instruction), the
// wheel brings it nearer or farther.
//
// Exports (demo.map): demo_init (the width must be a multiple of 32: four rays
// per instruction at 1 per 8 pixels), demo_theme, demo_pointer, demo_wheel,
// demo_command (1: back to the first view) and demo_frame, which returns the
// rays it traced (0 once the last pass is done) for the page's figures.
// =============================================================================

BSS
.p2align 4
frame:                  // RGBA, row by row, up to 1280 x 720
    .skip 3686400
ENDDATA frame
.p2align 4
colours:                // f32 RGB triples (0..255): spheres 0, 1, 2 at +0, +12, +24; the
    .skip 96            // floor's two greys at +36 and +48; the sky, low and high, at +60
ENDDATA colours         // and +72; the light's strength at +84
.p2align 4
state:                  // +0 width, +4 height, +8 the pass (0..3; 4: done), +12 its next block
    .skip 128           // row, +16 the camera's angle, +20 its distance, +24 its position,
ENDDATA state           // +36 forward, +48 right (x and z; its y is 0), +56 up (all f32 x, y,
                        // z), +68 the pointer's last x, +72 its buttons

// ---- Constants and the scene ------------------------------------------------
// llvm-mc's WebAssembly parser takes a decimal f32 only when it is short and
// exact (0.5, 40.0); the others are written as hex floats, the form it prints:
// 0x1.333334p0 is 1.2 rounded to f32.

// SPLAT value: an f32x4 of four copies of a constant.
.macro SPLAT value
    f32.const \value
    f32x4.splat
.endm

// STATE_SPLAT offset: an f32x4 of four copies of the f32 at state+offset.
.macro STATE_SPLAT offset
    ADDRESS state
    f32.load \offset
    f32x4.splat
.endm

// COLOUR_SPLAT offset: an f32x4 of four copies of the f32 at colours+offset.
.macro COLOUR_SPLAT offset
    ADDRESS colours
    f32.load \offset
    f32x4.splat
.endm

// SPHERE_DIST px, py, pz, cx, cy, cz, r, tmp: leaves |p - c| - r for the four
// points in locals px, py, pz (tmp: a scratch v128 local).
.macro SPHERE_DIST px, py, pz, cx, cy, cz, r, tmp
    local.get \px
    SPLAT \cx
    f32x4.sub
    local.tee \tmp
    local.get \tmp
    f32x4.mul
    local.get \py
    SPLAT \cy
    f32x4.sub
    local.tee \tmp
    local.get \tmp
    f32x4.mul
    f32x4.add
    local.get \pz
    SPLAT \cz
    f32x4.sub
    local.tee \tmp
    local.get \tmp
    f32x4.mul
    f32x4.add
    f32x4.sqrt
    SPLAT \r
    f32x4.sub
.endm

// The three spheres: (0, 1, 0) radius 1, (-2.3, 0.7, 1.1) radius 0.7, (2.1, 0.6, 0.9) radius 0.6.
.macro SPHERE0 px, py, pz, tmp
    SPHERE_DIST \px, \py, \pz, 0.0, 1.0, 0.0, 1.0, \tmp
.endm
.macro SPHERE1 px, py, pz, tmp
    SPHERE_DIST \px, \py, \pz, -0x1.266666p1, 0x1.666666p-1, 0x1.19999ap0, 0x1.666666p-1, \tmp
.endm
.macro SPHERE2 px, py, pz, tmp
    SPHERE_DIST \px, \py, \pz, 0x1.0cccccp1, 0x1.333334p-1, 0x1.ccccccp-1, 0x1.333334p-1, \tmp
.endm

// MAP_TAKE dist, mat, cand, mask, id: with a sphere's distance on the stack, keeps
// it (and the sphere's id) in the lanes where it is nearer than dist.
.macro MAP_TAKE dist, mat, cand, mask, id
    local.tee \cand
    local.get \dist
    f32x4.lt
    local.set \mask
    local.get \cand
    local.get \dist
    local.get \mask
    v128.bitselect
    local.set \dist
    i32.const \id
    i32x4.splat
    local.get \mat
    local.get \mask
    v128.bitselect
    local.set \mat
.endm

// MAP4 px, py, pz, dist, mat, tmp, cand, mask: the scene's distance at four points,
// and what is nearest (i32 lanes: 0..2 a sphere, 3 the floor).
.macro MAP4 px, py, pz, dist, mat, tmp, cand, mask
    local.get \py
    local.set \dist
    i32.const 3
    i32x4.splat
    local.set \mat
    SPHERE0 \px, \py, \pz, \tmp
    MAP_TAKE \dist, \mat, \cand, \mask, 0
    SPHERE1 \px, \py, \pz, \tmp
    MAP_TAKE \dist, \mat, \cand, \mask, 1
    SPHERE2 \px, \py, \pz, \tmp
    MAP_TAKE \dist, \mat, \cand, \mask, 2
.endm

// MAPD4 px, py, pz, dist, tmp: the scene's distance alone (the shadow march).
.macro MAPD4 px, py, pz, dist, tmp
    local.get \py
    SPHERE0 \px, \py, \pz, \tmp
    f32x4.min
    SPHERE1 \px, \py, \pz, \tmp
    f32x4.min
    SPHERE2 \px, \py, \pz, \tmp
    f32x4.min
    local.set \dist
.endm

// MAPD4_OTHERS px, py, pz, dist, tmp: the scene's distance without the lane's own
// sphere (masks in locals 43, 44, 47: sphere 0, 1, 2), for the shadow march. A
// sphere's own dark side comes from the diffuse term; marching it again made the
// shadow line jagged where the ray grazes the sphere it starts from.
.macro MAPD4_OTHERS px, py, pz, dist, tmp
    local.get \py
    SPLAT 1000.0
    SPHERE0 \px, \py, \pz, \tmp
    local.get 43
    v128.bitselect
    f32x4.min
    SPLAT 1000.0
    SPHERE1 \px, \py, \pz, \tmp
    local.get 44
    v128.bitselect
    f32x4.min
    SPLAT 1000.0
    SPHERE2 \px, \py, \pz, \tmp
    local.get 47
    v128.bitselect
    f32x4.min
    local.set \dist
.endm

// SELECT3 a, b, c, m0, m1: per lane, a where mask m0 is set, else b where m1 is,
// else c (constants).
.macro SELECT3 a, b, c, m0, m1
    SPLAT \a
    SPLAT \b
    SPLAT \c
    local.get \m1
    v128.bitselect
    local.get \m0
    v128.bitselect
.endm

// SELECT3C oa, ob, oc, m0, m1: the same with colours at colours+oa, +ob, +oc.
.macro SELECT3C oa, ob, oc, m0, m1
    COLOUR_SPLAT \oa
    COLOUR_SPLAT \ob
    COLOUR_SPLAT \oc
    local.get \m1
    v128.bitselect
    local.get \m0
    v128.bitselect
.endm

// CHANNEL s0, s1, s2, fa, fb, k0, k1, out: one colour channel of the four rays,
// from the colours' offsets for that channel: the sphere's or the floor's
// (checker in local 45) colour times the light (39) where the ray hit (37), the
// sky (low k0 to high k1 by local 46) where it did not.
.macro CHANNEL s0, s1, s2, fa, fb, k0, k1, out
    COLOUR_SPLAT \fa
    COLOUR_SPLAT \fb
    local.get 45
    v128.bitselect
    SELECT3C \s0, \s1, \s2, 43, 44
    local.get 38
    v128.bitselect
    local.get 39
    f32x4.mul
    COLOUR_SPLAT \k0
    COLOUR_SPLAT \k1
    COLOUR_SPLAT \k0
    f32x4.sub
    local.get 46
    f32x4.mul
    f32x4.add
    local.get 37
    v128.bitselect
    local.set \out
.endm

// WRITE_LANE lane: the colour of ray lane (local 29) into its s x s block of the
// frame, rows below the frame left out.
.macro WRITE_LANE lane
    local.get 29
    i32x4.extract_lane \lane
    local.set 7
    local.get 0
    i32.const \lane
    i32.add
    local.get 2
    i32.mul
    local.set 13
    i32.const 0
    local.set 8
    block
    loop
        local.get 12
        local.get 8
        i32.add
        local.get 11
        i32.ge_u
        br_if 1
        ADDRESS frame
        local.get 12
        local.get 8
        i32.add
        local.get 10
        i32.mul
        local.get 13
        i32.add
        i32.const 2
        i32.shl
        i32.add
        local.set 6
        i32.const 0
        local.set 9
        loop
            local.get 6
            local.get 9
            i32.const 2
            i32.shl
            i32.add
            local.get 7
            i32.store 0
            local.get 9
            i32.const 1
            i32.add
            local.tee 9
            local.get 2
            i32.lt_u
            br_if 0
        end_loop
        local.get 8
        i32.const 1
        i32.add
        local.tee 8
        local.get 2
        i32.lt_u
        br_if 0
    end_loop
    end_block
.endm

// -----------------------------------------------------------------------------
// sin_poly(x) -> sin x, to about 2e-4: x minus the nearest multiple of 2 pi,
// folded into [-pi/2, pi/2], then x - x^3/3! + x^5/5! - x^7/7! (Horner on x^2).
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
        f32.const 0x1.921fb6p1   // pi
        local.get 0
        f32.sub
        local.set 0
    end_if
    local.get 0
    f32.const -0x1.921fb6p0
    f32.lt
    if
        f32.const -0x1.921fb6p1
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

// -----------------------------------------------------------------------------
// setup_camera(): the camera from its angle a and distance d: at (d sin a, 2.4,
// -d cos a), looking at (0, 1, 0); forward f, right r = (f.z, 0, -f.x)
// normalised, up = r x f. Locals: 0..2 f, 3 a length.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION setup_camera, () -> ()
    .local f32, f32, f32, f32
    ADDRESS state
    ADDRESS state
    f32.load 16
    CALL sin_poly
    ADDRESS state
    f32.load 20
    f32.mul
    f32.store 24
    ADDRESS state
    f32.const 0x1.333334p1       // 2.4
    f32.store 28
    ADDRESS state
    ADDRESS state
    f32.load 16
    f32.const 0x1.921fb6p0       // cos a = sin(a + pi / 2)
    f32.add
    CALL sin_poly
    f32.neg
    ADDRESS state
    f32.load 20
    f32.mul
    f32.store 32
    ADDRESS state
    f32.load 24
    f32.neg
    local.set 0
    f32.const 1.0
    ADDRESS state
    f32.load 28
    f32.sub
    local.set 1
    ADDRESS state
    f32.load 32
    f32.neg
    local.set 2
    local.get 0
    local.get 0
    f32.mul
    local.get 1
    local.get 1
    f32.mul
    f32.add
    local.get 2
    local.get 2
    f32.mul
    f32.add
    f32.sqrt
    local.set 3
    local.get 0
    local.get 3
    f32.div
    local.set 0
    local.get 1
    local.get 3
    f32.div
    local.set 1
    local.get 2
    local.get 3
    f32.div
    local.set 2
    ADDRESS state
    local.get 0
    f32.store 36
    ADDRESS state
    local.get 1
    f32.store 40
    ADDRESS state
    local.get 2
    f32.store 44
    local.get 2                  // right: (f.z, 0, -f.x) / |(f.z, f.x)|
    local.get 2
    f32.mul
    local.get 0
    local.get 0
    f32.mul
    f32.add
    f32.sqrt
    local.set 3
    ADDRESS state
    local.get 2
    local.get 3
    f32.div
    f32.store 48
    ADDRESS state
    local.get 0
    f32.neg
    local.get 3
    f32.div
    f32.store 52
    ADDRESS state                // up = (-r.z f.y, r.z f.x - r.x f.z, r.x f.y)
    ADDRESS state
    f32.load 52
    f32.neg
    local.get 1
    f32.mul
    f32.store 56
    ADDRESS state
    ADDRESS state
    f32.load 52
    local.get 0
    f32.mul
    ADDRESS state
    f32.load 48
    local.get 2
    f32.mul
    f32.sub
    f32.store 60
    ADDRESS state
    ADDRESS state
    f32.load 48
    local.get 1
    f32.mul
    f32.store 64
ENDFUNCTION setup_camera

// restart(): back to the coarsest pass, first block row: the picture changed.
LOCAL_FUNCTION restart, () -> ()
    ADDRESS state
    i32.const 0
    i32.store 8
    ADDRESS state
    i32.const 0
    i32.store 12
ENDFUNCTION restart

// STORE_COLOUR offset, value: one f32 of the colours block.
.macro STORE_COLOUR offset, value
    ADDRESS colours
    f32.const \value
    f32.store \offset
.endm

// -----------------------------------------------------------------------------
// set_colours(dark): the scene's colours (0..255 per channel) and light.
//   spheres: teal #06706d, mint #3eea8c, amber #d99a3c (both themes)
//   light:  floor #e2e7e9 and #f3f5f6, sky white to #ecf1f2, light 1
//   dark:   floor #282e32 and #1b2023, sky #15191c to #2c3a40, light 0.9
// -----------------------------------------------------------------------------
LOCAL_FUNCTION set_colours, (i32) -> ()
    STORE_COLOUR 0, 6.0
    STORE_COLOUR 4, 112.0
    STORE_COLOUR 8, 109.0
    STORE_COLOUR 12, 62.0
    STORE_COLOUR 16, 234.0
    STORE_COLOUR 20, 140.0
    STORE_COLOUR 24, 217.0
    STORE_COLOUR 28, 154.0
    STORE_COLOUR 32, 60.0
    local.get 0
    if
        STORE_COLOUR 36, 40.0
        STORE_COLOUR 40, 46.0
        STORE_COLOUR 44, 50.0
        STORE_COLOUR 48, 27.0
        STORE_COLOUR 52, 32.0
        STORE_COLOUR 56, 35.0
        STORE_COLOUR 60, 21.0
        STORE_COLOUR 64, 25.0
        STORE_COLOUR 68, 28.0
        STORE_COLOUR 72, 44.0
        STORE_COLOUR 76, 58.0
        STORE_COLOUR 80, 64.0
        STORE_COLOUR 84, 0x1.ccccccp-1     // 0.9
    else
        STORE_COLOUR 36, 226.0
        STORE_COLOUR 40, 231.0
        STORE_COLOUR 44, 233.0
        STORE_COLOUR 48, 243.0
        STORE_COLOUR 52, 245.0
        STORE_COLOUR 56, 246.0
        STORE_COLOUR 60, 255.0
        STORE_COLOUR 64, 255.0
        STORE_COLOUR 68, 255.0
        STORE_COLOUR 72, 236.0
        STORE_COLOUR 76, 241.0
        STORE_COLOUR 80, 242.0
        STORE_COLOUR 84, 1.0
    end_if
ENDFUNCTION set_colours

// -----------------------------------------------------------------------------
// trace4(bx, by, s): the four rays of blocks bx..bx+3 of block row by, at s
// pixels per block, marched, shaded and written. A block's ray goes through its
// top-left pixel, so every pass aims at the same points.
// Locals: i32 3 the step count, 6 an address, 7 a colour, 8 a block row, 9 a
// block column, 10 width, 11 height, 12 the block's y, 13 its x; f32 14 v,
// 15 1.2 / height; v128 16..18 the direction, 19 t, 20 done, 21 what was hit
// (-1: the sky), 22..24 the point, 25 the distance, 26 what is nearest,
// 27 and 28 scratch, 29 a mask (then the colours), 30..32 the normal (then
// the shadow's point), 33 the diffuse light, 34 the shadow, 35 its distance,
// 36 its active lanes, 37 hit, 38 the floor, 39 the light, 40..42 R, G, B,
// 43 sphere 0, 44 sphere 1, 45 the checker, 46 the sky's height, 47 sphere 2.
// -----------------------------------------------------------------------------
LOCAL_FUNCTION trace4, (i32, i32, i32) -> ()
    .local i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, i32, f32, f32, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128, v128
    ADDRESS state
    i32.load 0
    local.set 10
    ADDRESS state
    i32.load 4
    local.set 11
    f32.const 0x1.333334p0          // 1.2: the field of view's slope, over the height
    local.get 11
    f32.convert_i32_s
    f32.div
    local.set 15
    local.get 1                     // v = (y - height / 2) * 1.2 / height
    local.get 2
    i32.mul
    f32.convert_i32_s
    local.get 11
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    f32.sub
    local.get 15
    f32.mul
    local.set 14
    local.get 0                     // u, per ray: ((bx + lane) * s - width / 2) * 1.2 / height
    i32x4.splat
    v128.const 0, 1, 2, 3
    i32x4.add
    local.get 2
    i32x4.splat
    i32x4.mul
    f32x4.convert_i32x4_s
    local.get 10
    f32.convert_i32_s
    f32.const 0.5
    f32.mul
    f32x4.splat
    f32x4.sub
    local.get 15
    f32x4.splat
    f32x4.mul
    local.set 27
    // d = f + v up + u right, then normalised
    ADDRESS state
    f32.load 36
    local.get 14
    ADDRESS state
    f32.load 56
    f32.mul
    f32.add
    f32x4.splat
    local.get 27
    STATE_SPLAT 48
    f32x4.mul
    f32x4.add
    local.set 16
    ADDRESS state
    f32.load 40
    local.get 14
    ADDRESS state
    f32.load 60
    f32.mul
    f32.add
    f32x4.splat
    local.set 17
    ADDRESS state
    f32.load 44
    local.get 14
    ADDRESS state
    f32.load 64
    f32.mul
    f32.add
    f32x4.splat
    local.get 27
    STATE_SPLAT 52
    f32x4.mul
    f32x4.add
    local.set 18
    local.get 16
    local.get 16
    f32x4.mul
    local.get 17
    local.get 17
    f32x4.mul
    f32x4.add
    local.get 18
    local.get 18
    f32x4.mul
    f32x4.add
    f32x4.sqrt
    local.set 27
    local.get 16
    local.get 27
    f32x4.div
    local.set 16
    local.get 17
    local.get 27
    f32x4.div
    local.set 17
    local.get 18
    local.get 27
    f32x4.div
    local.set 18

    // The march: each lane until it hits, passes 40 units, or 96 steps are up.
    SPLAT 0.0
    local.set 19
    i32.const 0
    i32x4.splat
    local.set 20
    i32.const -1
    i32x4.splat
    local.set 21
    i32.const 0
    local.set 3
    block
    loop
        STATE_SPLAT 24
        local.get 16
        local.get 19
        f32x4.mul
        f32x4.add
        local.set 22
        STATE_SPLAT 28
        local.get 17
        local.get 19
        f32x4.mul
        f32x4.add
        local.set 23
        STATE_SPLAT 32
        local.get 18
        local.get 19
        f32x4.mul
        f32x4.add
        local.set 24
        MAP4 22, 23, 24, 25, 26, 27, 28, 29
        local.get 25                // a hit: nearer than 0.001 t, on a lane still going
        local.get 19
        SPLAT 0x1.0624dep-10        // 0.001
        f32x4.mul
        f32x4.lt
        local.get 20
        v128.andnot
        local.set 29
        local.get 26
        local.get 21
        local.get 29
        v128.bitselect              // what the new hits hit
        local.set 21
        local.get 20
        local.get 29
        v128.or
        local.get 19
        SPLAT 40.0
        f32x4.gt
        v128.or
        local.set 20                // done: hit, or out to the sky
        local.get 19
        local.get 25
        local.get 20
        v128.andnot
        f32x4.add
        local.set 19                // t += distance, on the lanes still going
        local.get 20
        i32x4.all_true
        br_if 1
        local.get 3
        i32.const 1
        i32.add
        local.tee 3
        i32.const 96
        i32.lt_u
        br_if 0
    end_loop
    end_block

    // Shading: what each lane hit, its point and normal, the light and the shadow.
    local.get 21
    i32.const 0
    i32x4.splat
    i32x4.ge_s
    local.set 37                    // hit something (not the sky)
    local.get 21
    i32.const 0
    i32x4.splat
    i32x4.eq
    local.set 43
    local.get 21
    i32.const 1
    i32x4.splat
    i32x4.eq
    local.set 44
    local.get 21
    i32.const 2
    i32x4.splat
    i32x4.eq
    local.set 47
    local.get 21
    i32.const 3
    i32x4.splat
    i32x4.eq
    local.set 38                    // the floor
    STATE_SPLAT 24
    local.get 16
    local.get 19
    f32x4.mul
    f32x4.add
    local.set 22
    STATE_SPLAT 28
    local.get 17
    local.get 19
    f32x4.mul
    f32x4.add
    local.set 23
    STATE_SPLAT 32
    local.get 18
    local.get 19
    f32x4.mul
    f32x4.add
    local.set 24
    // The normal: (p - c) / r for the lane's sphere, (0, 1, 0) on the floor.
    SELECT3 1.0, 0x1.666666p-1, 0x1.333334p-1, 43, 44
    local.set 27                    // r
    SPLAT 0.0                       // the floor's normal, where the mask is set...
    local.get 22
    SELECT3 0.0, -0x1.266666p1, 0x1.0cccccp1, 43, 44
    f32x4.sub
    local.get 27
    f32x4.div                       // ...the sphere's elsewhere
    local.get 38
    v128.bitselect
    local.set 30
    SPLAT 1.0                       // the floor's normal, where the mask is set...
    local.get 23
    SELECT3 1.0, 0x1.666666p-1, 0x1.333334p-1, 43, 44
    f32x4.sub
    local.get 27
    f32x4.div                       // ...the sphere's elsewhere
    local.get 38
    v128.bitselect
    local.set 31
    SPLAT 0.0                       // the floor's normal, where the mask is set...
    local.get 24
    SELECT3 0.0, 0x1.19999ap0, 0x1.ccccccp-1, 43, 44
    f32x4.sub
    local.get 27
    f32x4.div                       // ...the sphere's elsewhere
    local.get 38
    v128.bitselect
    local.set 32
    // diffuse = max(0, n . L), L = (-0.5, 0.8, -0.35) normalised
    local.get 30
    SPLAT -0x1.fcd466p-2
    f32x4.mul
    local.get 31
    SPLAT 0x1.971052p-1
    f32x4.mul
    f32x4.add
    local.get 32
    SPLAT -0x1.642e48p-2
    f32x4.mul
    f32x4.add
    SPLAT 0.0
    f32x4.max
    local.set 33
    // The soft shadow: march towards the light, from a point 0.01 off the surface along
    // its normal (from the surface itself, a ray grazing the sphere near its shadow line
    // found the sphere again, and the line came out jagged), starting 0.02 along the ray.
    // Only lanes on the floor, or on the lit side of a sphere: the dark side is 0.3 lit
    // whatever the shadow says.
    local.get 22
    local.get 30
    SPLAT 0x1.47ae14p-7             // 0.01
    f32x4.mul
    f32x4.add
    local.set 22
    local.get 23
    local.get 31
    SPLAT 0x1.47ae14p-7
    f32x4.mul
    f32x4.add
    local.set 23
    local.get 24
    local.get 32
    SPLAT 0x1.47ae14p-7
    f32x4.mul
    f32x4.add
    local.set 24
    SPLAT 1.0
    local.set 34
    SPLAT 0x1.47ae14p-6             // 0.02
    local.set 35
    local.get 37
    local.get 33
    SPLAT 0.0
    f32x4.gt
    local.get 38
    v128.or
    v128.and
    local.set 36
    i32.const 0
    local.set 3
    block
    loop
        local.get 36
        v128.any_true
        i32.eqz
        br_if 1
        local.get 22
        SPLAT -0x1.fcd466p-2
        local.get 35
        f32x4.mul
        f32x4.add
        local.set 30
        local.get 23
        SPLAT 0x1.971052p-1
        local.get 35
        f32x4.mul
        f32x4.add
        local.set 31
        local.get 24
        SPLAT -0x1.642e48p-2
        local.get 35
        f32x4.mul
        f32x4.add
        local.set 32
        MAPD4_OTHERS 30, 31, 32, 25, 27
        local.get 25                // blocked: something nearer than 0.001
        SPLAT 0x1.0624dep-10
        f32x4.lt
        local.get 36
        v128.and
        local.set 29
        local.get 34
        local.get 29
        v128.andnot
        local.set 34                // a blocked lane is in full shadow
        local.get 36
        local.get 29
        v128.andnot
        local.set 36
        local.get 34                // sh = min(sh, 10 h / s) on the lanes still going
        SPLAT 10.0
        local.get 25
        f32x4.mul
        local.get 35
        f32x4.div
        f32x4.min
        local.get 34
        local.get 36
        v128.bitselect
        local.set 34
        local.get 35
        local.get 25
        local.get 36
        v128.and
        f32x4.add
        local.set 35                // s += h
        local.get 36
        local.get 35
        SPLAT 12.0
        f32x4.lt
        v128.and
        local.set 36                // and stop past 12
        local.get 3
        i32.const 1
        i32.add
        local.tee 3
        i32.const 40
        i32.lt_u
        br_if 0
    end_loop
    end_block
    // The light: 0.55 + 0.45 sh on the floor, 0.3 + 0.7 diffuse sh on a sphere.
    SPLAT 0x1.19999ap-1             // 0.55
    SPLAT 0x1.ccccccp-2             // 0.45
    local.get 34
    f32x4.mul
    f32x4.add
    SPLAT 0x1.333334p-2             // 0.3
    SPLAT 0x1.666666p-1             // 0.7
    local.get 33
    f32x4.mul
    local.get 34
    f32x4.mul
    f32x4.add
    local.get 38
    v128.bitselect
    COLOUR_SPLAT 84
    f32x4.mul
    local.set 39
    // The floor's checker: (floor x + floor z) odd.
    local.get 22
    f32x4.floor
    i32x4.trunc_sat_f32x4_s
    local.get 24
    f32x4.floor
    i32x4.trunc_sat_f32x4_s
    i32x4.add
    i32.const 1
    i32x4.splat
    v128.and
    i32.const 1
    i32x4.splat
    i32x4.eq
    local.set 45
    // The sky's height: clamp(2 d.y, 0, 1).
    local.get 17
    SPLAT 2.0
    f32x4.mul
    SPLAT 0.0
    f32x4.max
    SPLAT 1.0
    f32x4.min
    local.set 46
    CHANNEL 0, 12, 24, 36, 48, 60, 72, 40
    CHANNEL 4, 16, 28, 40, 52, 64, 76, 41
    CHANNEL 8, 20, 32, 44, 56, 68, 80, 42
    // RGBA words: R | G << 8 | B << 16 | 255 << 24.
    local.get 40
    i32x4.trunc_sat_f32x4_u
    local.get 41
    i32x4.trunc_sat_f32x4_u
    i32.const 8
    i32x4.shl
    v128.or
    local.get 42
    i32x4.trunc_sat_f32x4_u
    i32.const 16
    i32x4.shl
    v128.or
    i32.const 0xFF000000
    i32x4.splat
    v128.or
    local.set 29
    local.get 1
    local.get 2
    i32.mul
    local.set 12                    // the blocks' top row
    WRITE_LANE 0
    WRITE_LANE 1
    WRITE_LANE 2
    WRITE_LANE 3
ENDFUNCTION trace4

// -----------------------------------------------------------------------------
// demo_init(width, height) -> the frame's address, or 0 when the width is not a
// multiple of 32 or the frame is empty or larger than 1280 x 720. The camera
// starts at angle 0.6, 6.5 units from the middle sphere; light colours.
// -----------------------------------------------------------------------------
FUNCTION demo_init, (i32, i32) -> (i32)
    local.get 0
    i32.const 32
    i32.lt_s
    local.get 1
    i32.const 1
    i32.lt_s
    i32.or
    local.get 0
    i32.const 31
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
    f32.const 0x1.333334p-1         // 0.6
    f32.store 16
    ADDRESS state
    f32.const 6.5
    f32.store 20
    ADDRESS state
    i32.const 0
    i32.store 72
    CALL setup_camera
    i32.const 0
    CALL set_colours
    CALL restart
    ADDRESS frame
ENDFUNCTION demo_init

// demo_theme(dark): the colours, light (0) or dark (1); the picture starts over.
FUNCTION demo_theme, (i32) -> ()
    local.get 0
    CALL set_colours
    CALL restart
ENDFUNCTION demo_theme

// -----------------------------------------------------------------------------
// demo_pointer(x, y, buttons): dragging with the main button turns the camera
// around the scene, 0.01 radian per pixel.
// -----------------------------------------------------------------------------
FUNCTION demo_pointer, (f32, f32, i32) -> ()
    local.get 2
    ADDRESS state
    i32.load 72
    i32.and
    i32.const 1
    i32.and
    if
        ADDRESS state
        ADDRESS state
        f32.load 16
        local.get 0
        ADDRESS state
        f32.load 68
        f32.sub
        f32.const 0x1.47ae14p-7     // 0.01
        f32.mul
        f32.add
        f32.store 16
        CALL setup_camera
        CALL restart
    end_if
    ADDRESS state
    local.get 0
    f32.store 68
    ADDRESS state
    local.get 2
    i32.store 72
ENDFUNCTION demo_pointer

// -----------------------------------------------------------------------------
// demo_wheel(x, y, delta): the camera 1.12 times nearer (delta < 0) or farther,
// between 3.5 and 14 units from the middle sphere.
// -----------------------------------------------------------------------------
FUNCTION demo_wheel, (f32, f32, f32) -> ()
    ADDRESS state
    ADDRESS state
    f32.load 20
    local.get 2
    f32.const 0.0
    f32.lt
    if f32
        f32.const 0x1.c92492p-1     // 1 / 1.12
    else
        f32.const 0x1.1eb852p0      // 1.12
    end_if
    f32.mul
    f32.const 0x1.cp1               // 3.5
    f32.max
    f32.const 14.0
    f32.min
    f32.store 20
    CALL setup_camera
    CALL restart
ENDFUNCTION demo_wheel

// demo_command(command): 1 brings the first view back.
FUNCTION demo_command, (i32) -> ()
    local.get 0
    i32.const 1
    i32.eq
    if
        ADDRESS state
        f32.const 0x1.333334p-1
        f32.store 16
        ADDRESS state
        f32.const 6.5
        f32.store 20
        CALL setup_camera
        CALL restart
    end_if
ENDFUNCTION demo_command

// -----------------------------------------------------------------------------
// demo_frame() -> the rays traced: block rows of the current pass until about
// 16,000 rays are done or the last pass is complete (then 0). A pass of scale s
// = 8 >> pass has width / s blocks a row and ceil(height / s) rows.
// Locals: 0 the rays, 1 s, 2 the blocks a row, 3 the block rows, 4 bx.
// -----------------------------------------------------------------------------
FUNCTION demo_frame, () -> (i32)
    .local i32, i32, i32, i32, i32
    i32.const 0
    local.set 0
    block
    loop
        ADDRESS state
        i32.load 8
        i32.const 4
        i32.ge_u
        br_if 1                     // every pass is done
        local.get 0
        i32.const 16384
        i32.ge_u
        br_if 1                     // this frame's share is done
        i32.const 8
        ADDRESS state
        i32.load 8
        i32.shr_u
        local.set 1
        ADDRESS state
        i32.load 0
        local.get 1
        i32.div_u
        local.set 2
        ADDRESS state
        i32.load 4
        local.get 1
        i32.add
        i32.const 1
        i32.sub
        local.get 1
        i32.div_u
        local.set 3
        i32.const 0
        local.set 4
        loop                        // one block row, four rays at a time
            local.get 4
            ADDRESS state
            i32.load 12
            local.get 1
            CALL trace4
            local.get 4
            i32.const 4
            i32.add
            local.tee 4
            local.get 2
            i32.lt_u
            br_if 0
        end_loop
        local.get 0
        local.get 2
        i32.add
        local.set 0
        ADDRESS state
        ADDRESS state
        i32.load 12
        i32.const 1
        i32.add
        local.tee 4
        i32.store 12
        local.get 4
        local.get 3
        i32.ge_u
        if                          // the pass is complete: the next, finer one
            ADDRESS state
            ADDRESS state
            i32.load 8
            i32.const 1
            i32.add
            i32.store 8
            ADDRESS state
            i32.const 0
            i32.store 12
        end_if
        br 0
    end_loop
    end_block
    local.get 0
ENDFUNCTION demo_frame
