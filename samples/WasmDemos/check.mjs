// check.mjs - checks the Wasm Demos' kernels under Node, against what each must do, and prints
// one line per check ("ok ..." or "FAIL ..."); exits 1 if any check failed. ExampleTests runs it
// on the modules the build just wrote. Usage:
//   node check.mjs <life.wasm> [<swarm.wasm>] [<ray.wasm>]
// (a module given as "-" is skipped). The modules export only the demo interface (demo.map), so
// the checks reach the kernels' state through the memory, at the addresses the layout of each
// .wasm.s gives: the BSS keeps its declaration order (wasm-ld places one object's .bss in order),
// which the first Life check confirms before the others rely on it.
import { readFileSync } from 'node:fs';

let failures = 0;
/**
 * Records one check.
 * @param {boolean} ok whether it held.
 * @param {string} what what was checked.
 */
function check(ok, what) {
  console.log(`${ok ? 'ok' : 'FAIL'} ${what}`);
  if (!ok) failures++;
}

/**
 * Instantiates a demo module from a file (it imports nothing).
 * @param {string} path the .wasm file.
 * @returns {Promise<WebAssembly.Exports>} its exports.
 */
async function load(path) {
  const { instance } = await WebAssembly.instantiate(readFileSync(path), {});
  return instance.exports;
}

/**
 * Steps a 4096 x 4096 torus one generation with Conway's rule, cell by cell: the reference the
 * bit-sliced kernel is compared with.
 * @param {Uint8Array} a the board, one byte per cell (0 or 1), row-major.
 * @returns {Uint8Array} the next generation.
 */
function lifeReference(a) {
  const N = 4096, b = new Uint8Array(N * N);
  for (let y = 0; y < N; y++) {
    const ym = ((y + N - 1) & (N - 1)) * N, y0 = y * N, yp = ((y + 1) & (N - 1)) * N;
    for (let x = 0; x < N; x++) {
      const xm = (x + N - 1) & (N - 1), xp = (x + 1) & (N - 1);
      const n = a[ym + xm] + a[ym + x] + a[ym + xp] + a[y0 + xm] + a[y0 + xp] + a[yp + xm] + a[yp + x] + a[yp + xp];
      b[y0 + x] = (n === 3 || (n === 2 && a[y0 + x] === 1)) ? 1 : 0;
    }
  }
  return b;
}

/**
 * The Life kernel: its layout, its rule on known patterns and on a random board (against the
 * reference, wrap-around included), and its drawing.
 * @param {string} path life.wasm.
 */
async function checkLife(path) {
  const m = await load(path);
  const frame = m.demo_init(960, 540);
  check(frame > 0, `life: demo_init(960, 540) returns the frame (${frame})`);
  check(m.demo_init(1281, 720) === 0, 'life: a frame larger than 1280 x 720 is refused');
  m.demo_init(960, 540);
  // The layout of life.wasm.s's BSS: board_a (2 MiB), board_b (2 MiB), frame (1280 x 720 x 4),
  // palette (272 bytes), state; all 16-aligned, so the frame's address gives the others.
  const boardA = frame - 4194304, state = frame + 3686400 + 272;
  /**
   * The module's memory as bytes. A new view each time: the kernel never grows its memory, but
   * a view must not outlive a grow.
   * @returns {Uint8Array} the bytes.
   */
  const mem = () => new Uint8Array(m.memory.buffer);
  /**
   * The module's memory for little-endian word reads.
   * @returns {DataView} the view.
   */
  const view = () => new DataView(m.memory.buffer);
  check(view().getUint32(state, true) === boardA, 'life: the layout is board_a, board_b, frame, palette, state');
  /**
   * The current board's address (state+0), which demo_frame swaps between the two boards.
   * @returns {number} the address.
   */
  const current = () => view().getUint32(state, true);
  /**
   * Unpacks a board: bit x & 7 of byte y * 512 + x / 8 is cell (x, y).
   * @param {number} board the board's address.
   * @returns {Uint8Array} one byte per cell, row-major.
   */
  const cellsOf = (board) => {
    const bits = mem().subarray(board, board + 2097152), cells = new Uint8Array(4096 * 4096);
    for (let i = 0; i < cells.length; i++) cells[i] = (bits[i >> 3] >> (i & 7)) & 1;
    return cells;
  };
  /**
   * Brings one cell to life, directly in the board's memory.
   * @param {number} board the board's address.
   * @param {number} x the cell's column (0..4095).
   * @param {number} y its row (0..4095).
   */
  const setCell = (board, x, y) => { mem()[board + y * 512 + (x >> 3)] |= 1 << (x & 7); };

  // A random board (demo_init's soup): three generations against the reference, every cell.
  let ref = cellsOf(current());
  const density = ref.reduce((s, c) => s + c, 0) / ref.length;
  check(density > 0.35 && density < 0.40, `life: the soup holds 3/8 of the cells (${density.toFixed(4)})`);
  for (let g = 1; g <= 3; g++) {
    const work = m.demo_frame();
    ref = lifeReference(ref);
    const got = cellsOf(current());
    let diff = 0;
    for (let i = 0; i < got.length; i++) if (got[i] !== ref[i]) diff++;
    check(diff === 0 && work === 16777216, `life: generation ${g} of the soup equals the reference (${diff} cells differ, work ${work})`);
  }

  // A glider across the corner where the board wraps: after 4 generations it is the same shape,
  // one cell right and one down, now straddling the edges.
  m.demo_command(2);
  check(cellsOf(current()).every((c) => c === 0), 'life: demo_command(2) clears the board');
  const glider = [[1, 0], [2, 1], [0, 2], [1, 2], [2, 2]];
  for (const [x, y] of glider) setCell(current(), (4094 + x) & 4095, (4094 + y) & 4095);
  for (let g = 0; g < 4; g++) m.demo_frame();
  const after = cellsOf(current());
  const expected = new Set(glider.map(([x, y]) => (((4095 + y) & 4095) * 4096) + ((4095 + x) & 4095)));
  let ok = after.reduce((s, c) => s + c, 0) === 5;
  for (const i of expected) ok = ok && after[i] === 1;
  check(ok, 'life: a glider moves one cell right and down every 4 generations, across the wrap');

  // A blinker: period 2, and the pause command stops it.
  m.demo_command(2);
  for (const x of [100, 101, 102]) setCell(current(), x, 200);
  m.demo_frame();
  let c = cellsOf(current());
  check(c[199 * 4096 + 101] && c[200 * 4096 + 101] && c[201 * 4096 + 101] && !c[200 * 4096 + 100], 'life: a blinker turns vertical');
  m.demo_command(3);
  check(m.demo_frame() === 0, 'life: paused, demo_frame steps nothing');
  m.demo_command(3);
  m.demo_frame();
  c = cellsOf(current());
  check(c[200 * 4096 + 100] && c[200 * 4096 + 101] && c[200 * 4096 + 102] && !c[199 * 4096 + 101], 'life: and back, horizontal');

  // Drawing: the view's centre is cell (2048, 2048) at frame pixel (480, 270), one pixel per cell.
  m.demo_command(2);
  setCell(current(), 2048, 2048);
  m.demo_command(3);
  m.demo_frame();
  /**
   * A frame pixel as a little-endian RGBA word (0xAABBGGRR).
   * @param {number} x the pixel's column.
   * @param {number} y its row.
   * @returns {number} the word.
   */
  const px = (x, y) => view().getUint32(frame + (y * 960 + x) * 4, true);
  check(px(480, 270) === 0xFF2E2F06 && px(481, 270) === 0xFFFFFFFF, 'life: a live cell is ink (#062f2e) on white, at the centre pixel');
  m.demo_theme(1);
  m.demo_frame();
  check(px(480, 270) === 0xFF8CEA3E && px(481, 270) === 0xFF1C1915, 'life: dark theme, mint on #15191c');
  m.demo_theme(0);
  m.demo_pointer(10, 10, 2);
  m.demo_frame();
  check(px(10, 10) === 0xFF2E2F06 && px(11, 11) === 0xFF2E2F06 && px(12, 12) === 0xFFFFFFFF, 'life: the secondary button draws a 3 x 3 block under the pointer');
  m.demo_pointer(100, 100, 1);
  m.demo_pointer(110, 100, 1);
  m.demo_frame();
  check(px(490, 270) === 0xFF2E2F06, 'life: a drag with the main button pans the view (10 pixels right)');
  m.demo_wheel(490, 270, -1);
  m.demo_frame();
  check(px(490, 270) === 0xFF2E2F06 && px(491, 270) === 0xFF2E2F06 && px(492, 270) === 0xFFFFFFFF, 'life: the wheel zooms in at the pointer (2 pixels per cell)');
  for (let i = 0; i < 5; i++) m.demo_wheel(490, 270, 1);
  m.demo_frame();
  check(view().getUint32(state + 12, true) === -3 >>> 0, 'life: zooming out stops at 8 cells per pixel');
}

/**
 * One step of the swarm's physics in single precision, operation by operation as swarm.wasm.s
 * does it (JavaScript computes in double and Math.fround rounds each result to f32, which gives
 * exactly the f32 result for +, -, *, / and the comparisons), on arrays of the module's layout.
 * @param {Float32Array} x the x positions (updated in place); y, vx, vy likewise.
 * @param {Float32Array} y the y positions.
 * @param {Float32Array} vx the x velocities.
 * @param {Float32Array} vy the y velocities.
 * @param {number} ax the attractor's x (an f32 value).
 * @param {number} ay its y.
 * @param {number} k the strength (60 pulls, -90 pushes).
 * @param {number} w the frame's width.
 * @param {number} h its height.
 */
function swarmReference(x, y, vx, vy, ax, ay, k, w, h) {
  const f = Math.fround, damp = f(0.993);
  for (let i = 0; i < x.length; i++) {
    const dx = f(ax - x[i]), dy = f(ay - y[i]);
    const s = f(k / f(f(f(dx * dx) + f(dy * dy)) + 900));
    let nvx = f(f(vx[i] + f(dx * s)) * damp), nvy = f(f(vy[i] + f(dy * s)) * damp);
    const nx = f(x[i] + nvx), ny = f(y[i] + nvy);
    if (nx < 0 || nx >= w) nvx = -nvx;
    if (ny < 0 || ny >= h) nvy = -nvy;
    x[i] = Math.min(Math.max(nx, 0), w - 1);
    y[i] = Math.min(Math.max(ny, 0), h - 1);
    vx[i] = nvx;
    vy[i] = nvy;
  }
}

/**
 * The density one particle adds at a count, as swarm.wasm.s's set_count works it out in f32:
 * 18 * sqrt(120,000 / count), the division, the square root and the product each rounded to f32
 * (correctly rounded in double, then once to f32, which is the f32 result for / and sqrt).
 * @param {number} count the particle count.
 * @returns {number} the f32 value the module stores.
 */
function swarmShare(count) {
  const f = Math.fround;
  return f(f(Math.sqrt(f(120000 / count))) * 18);
}

/**
 * The swarm kernel: the scattered start, one step against the f32-exact reference (pointer
 * attractor), the figure of eight (its sine and cosine, to the polynomial's precision), the
 * push, the density and the colours, then the particle counts the page offers (240,000 and
 * 500,000 and back), each stepped against the reference, and the commands it must ignore.
 * @param {string} path swarm.wasm.
 */
async function checkSwarm(path) {
  const m = await load(path);
  const W = 960, H = 540, N = 120000, STRIDE = 2000000;
  const frame = m.demo_init(W, H);
  check(frame > 0 && m.demo_init(962, 540) === 0, 'swarm: demo_init takes 960 x 540, refuses a width that is not a multiple of 4');
  m.demo_init(W, H);
  // The layout of swarm.wasm.s's BSS: pos_x, pos_y, vel_x, vel_y (2,000,000 bytes each: room for
  // 500,000 particles), density (1280 x 720 f32), frame, colours (1 KiB), state (+36 the count,
  // +56 the density a particle adds).
  const posX = frame - 3686400 - 4 * STRIDE, density = frame - 3686400, colours = frame + 3686400, state = colours + 1024;
  /**
   * The four particle arrays, as views into the module's memory.
   * @param {number} [n] how many particles to view (the default count unless given).
   * @returns {Float32Array[]} x, y, vx, vy.
   */
  const arrays = (n = N) => [0, 1, 2, 3].map((i) => new Float32Array(m.memory.buffer, posX + i * STRIDE, n));
  const R = Math.fround(Math.fround(540) * Math.fround(0.42));
  /**
   * Whether particles first..end-1 are where scatter_range puts them: on the ring around the
   * centre, moving round it at 1.4 pixels a frame.
   * @param {number} first the first particle.
   * @param {number} end one past the last.
   * @returns {boolean} true when all of them are.
   */
  const onRing = (first, end) => {
    const [x, y, vx, vy] = arrays(end);
    for (let i = first; i < end; i++) {
      const r = Math.hypot(x[i] - W / 2, y[i] - H / 2);
      if (r > R + 0.01 || r < R * 0.07 - 0.01 || Math.abs(Math.hypot(vx[i], vy[i]) - 1.4) >= 1e-4) return false;
    }
    return true;
  };
  let [x, y, vx, vy] = arrays();
  check(onRing(0, N), 'swarm: scattered on the ring around the centre, each moving round it at 1.4 pixels a frame');

  // One step towards the pointer, against the reference, bit for bit.
  const rx = Float32Array.from(x), ry = Float32Array.from(y), rvx = Float32Array.from(vx), rvy = Float32Array.from(vy);
  m.demo_pointer(300, 200, 0);
  check(m.demo_frame() === N, 'swarm: demo_frame moves 120,000 particles');
  swarmReference(rx, ry, rvx, rvy, 300, 200, 60, W, H);
  [x, y, vx, vy] = arrays();
  let diff = 0;
  for (let i = 0; i < N; i++) if (x[i] !== rx[i] || y[i] !== ry[i] || vx[i] !== rvx[i] || vy[i] !== rvy[i]) diff++;
  check(diff === 0, `swarm: one step towards the pointer equals the f32 reference, bit for bit (${diff} particles differ)`);

  // The push (main button): k = -90.
  m.demo_pointer(300, 200, 1);
  m.demo_frame();
  swarmReference(rx, ry, rvx, rvy, 300, 200, -90, W, H);
  [x, y, vx, vy] = arrays();
  diff = 0;
  for (let i = 0; i < N; i++) if (x[i] !== rx[i] || vx[i] !== rvx[i]) diff++;
  check(diff === 0, `swarm: with the main button down the swarm is pushed away, as the reference says (${diff} differ)`);

  // The figure of eight (no pointer): frame 3 aims at (480 + cos(0.033) * 288, 270 + sin(0.066) * 151.2).
  m.demo_pointer(-1, -1, 0);
  m.demo_frame();
  const ax = Math.fround(480 + Math.fround(Math.cos(Math.fround(0.033)) * 288));
  const ay = Math.fround(270 + Math.fround(Math.sin(Math.fround(0.066)) * Math.fround(151.2)));
  swarmReference(rx, ry, rvx, rvy, ax, ay, 60, W, H);
  [x, y, vx, vy] = arrays();
  let worst = 0;
  for (let i = 0; i < N; i++) worst = Math.max(worst, Math.abs(vx[i] - rvx[i]), Math.abs(vy[i] - rvy[i]));
  check(worst < 1e-3, `swarm: without a pointer it follows the figure of eight: sin_poly agrees with Math.sin (velocities within ${worst.toExponential(1)})`);

  // The density and the colours: the frame shows what the density says.
  const dens = new Float32Array(m.memory.buffer, density, W * H), pal = new Uint32Array(m.memory.buffer, colours, 256);
  const px = new Uint32Array(m.memory.buffer, frame, W * H);
  let colourOk = true, busy = 0;
  for (let i = 0; i < W * H; i += 7) {
    colourOk = colourOk && px[i] === pal[Math.min(255, Math.trunc(dens[i]))];
    if (dens[i] > 0) busy++;
  }
  check(colourOk && busy > 1000, `swarm: each pixel's colour is the density's (${busy} sampled pixels touched)`);
  check(pal[0] === 0xFFFFFFFF && pal[255] === 0xFF2E2F06, 'swarm: the light colours run from white to ink (#062f2e)');
  m.demo_theme(1);
  check(pal[0] === 0xFF1C1915 && pal[255] === 0xFFF4F5F2 && pal[85] === 0xFF6D7006, 'swarm: the dark ones from #15191c through teal and mint to #f2f5f4');
  m.demo_command(3);
  check(m.demo_frame() === 0, 'swarm: paused, demo_frame moves nothing');
  m.demo_command(3);

  // The page's particle counts: each one a command of its own (the count itself).
  const view = () => new DataView(m.memory.buffer);
  check(view().getInt32(state + 36, true) === N && view().getFloat32(state + 56, true) === 18,
    'swarm: a page starts at 120,000 particles, each adding 18 to the density');
  m.demo_pointer(300, 200, 0);
  for (const count of [240000, 500000]) {
    const before = view().getInt32(state + 36, true);
    const kept = arrays(before).map((a) => Float32Array.from(a));
    m.demo_command(count);
    const now = arrays(before);
    const untouched = kept.every((a, k) => a.every((v, i) => Object.is(v, now[k][i])));
    check(view().getInt32(state + 36, true) === count && view().getFloat32(state + 56, true) === swarmShare(count) && untouched && onRing(before, count),
      `swarm: ${count.toLocaleString('en-US')} particles: the swarm stays, the newcomers are scattered on the ring, each adds ${swarmShare(count).toFixed(3)}`);
    // One step of all of them against the reference, and the density they add: with the fade by
    // 0.78 applied first, the image's total grows by count * share (each particle lands on one
    // pixel, clamped into the frame).
    const dens = new Float32Array(m.memory.buffer, density, W * H);
    let total = 0;
    for (const d of dens) total += d;
    const [rx, ry, rvx, rvy] = arrays(count).map((a) => Float32Array.from(a));
    check(m.demo_frame() === count, `swarm: demo_frame moves ${count.toLocaleString('en-US')} particles`);
    swarmReference(rx, ry, rvx, rvy, 300, 200, 60, W, H);
    [x, y, vx, vy] = arrays(count);
    diff = 0;
    for (let i = 0; i < count; i++) if (x[i] !== rx[i] || y[i] !== ry[i] || vx[i] !== rvx[i] || vy[i] !== rvy[i]) diff++;
    check(diff === 0, `swarm: one step of ${count.toLocaleString('en-US')} particles equals the f32 reference, bit for bit (${diff} differ)`);
    let after = 0;
    for (const d of new Float32Array(m.memory.buffer, density, W * H)) after += d;
    const expected = total * Math.fround(0.78) + count * swarmShare(count);
    check(Math.abs(after - expected) <= expected * 1e-4,
      `swarm: at ${count.toLocaleString('en-US')} each frame adds count * share to the density (${after.toFixed(0)} against ${expected.toFixed(0)})`);
  }
  // Back to 120,000: the particles past it stay where they were, undrawn and unmoved.
  const rest = arrays(500000).map((a) => Float32Array.from(a.subarray(N)));
  m.demo_command(N);
  check(m.demo_frame() === N && view().getFloat32(state + 56, true) === 18, 'swarm: back at 120,000, demo_frame moves 120,000, each adding 18 again');
  const tail = arrays(500000).map((a) => a.subarray(N));
  check(rest.every((a, k) => a.every((v, i) => Object.is(v, tail[k][i]))), 'swarm: the particles a smaller count drops are not moved');
  // Growing again scatters the newcomers anew, wherever the dropped ones had got to.
  m.demo_command(240000);
  check(onRing(N, 240000), 'swarm: growing again scatters the particles it adds on the ring afresh');
  m.demo_command(N);
  // Commands that are no count: past the arrays' room, not a multiple of 4, too small, negative.
  for (const command of [500004, 240001, 2, 0, -4, 1000000]) m.demo_command(command);
  check(view().getInt32(state + 36, true) === N, 'swarm: commands that are no count (500,004, 240,001, 2, 0, -4, 1,000,000) leave it at 120,000');
}

/**
 * Rounds half to even, as WebAssembly's f32.nearest does (Math.round rounds half up).
 * @param {number} x an f32 value.
 * @returns {number} the nearest integer, ties to even.
 */
function nearestEven(x) {
  const r = Math.round(x);
  return Math.abs(x % 1) === 0.5 ? 2 * Math.round(x / 2) : r;
}

/**
 * ray.wasm.s's sin_poly, operation by operation in f32: x minus the nearest multiple of 2 pi,
 * folded into [-pi/2, pi/2], then the degree-7 polynomial by Horner's rule.
 * @param {number} x an f32 value.
 * @returns {number} sin x as the kernel computes it.
 */
function sinPoly(x) {
  const f = Math.fround;
  const INV2PI = f(1 / (2 * Math.PI)), TWOPI = f(2 * Math.PI), HALFPI = f(Math.PI / 2), PI = f(Math.PI);
  x = f(x - f(nearestEven(f(x * INV2PI)) * TWOPI));
  if (x > HALFPI) x = f(PI - x);
  if (x < -HALFPI) x = f(-PI - x);
  const x2 = f(x * x);
  let a = f(x2 * f(-1 / 5040));
  a = f(f(1 / 120) + a);
  a = f(x2 * a);
  a = f(f(-1 / 6) + a);
  a = f(x2 * a);
  a = f(1 + a);
  return f(x * a);
}

/**
 * The ray kernel's picture, pixel by pixel in f32, in the kernel's order of operations: its
 * camera, its march, its normal, light, soft shadow and colours. The reference the module's last
 * pass (one ray per pixel) is compared with.
 * @param {number} W the frame's width.
 * @param {number} H its height.
 * @param {number} ang the camera's angle.
 * @param {number} dist its distance.
 * @param {boolean} dark the dark colours.
 * @returns {function(number, number): number} the RGBA word of pixel (x, y).
 */
function rayReference(W, H, ang, dist, dark) {
  const f = Math.fround, sqrt = (v) => f(Math.sqrt(v));
  const camx = f(sinPoly(ang) * dist), camy = f(2.4), camz = f(-sinPoly(f(ang + f(Math.PI / 2))) * dist);
  let fx = -camx, fy = f(1 - camy), fz = -camz;
  const len = sqrt(f(f(f(fx * fx) + f(fy * fy)) + f(fz * fz)));
  fx = f(fx / len); fy = f(fy / len); fz = f(fz / len);
  const len2 = sqrt(f(f(fz * fz) + f(fx * fx)));
  const rx = f(fz / len2), rz = f(-fx / len2);
  const ux = f(-rz * fy), uy = f(f(rz * fx) - f(rx * fz)), uz = f(rx * fy);
  const S = [[0, 1, 0, 1], [f(-2.3), f(0.7), f(1.1), f(0.7)], [f(2.1), f(0.6), f(0.9), f(0.6)]];
  const L = [f(-0.5 / Math.sqrt(1.0125)), f(0.8 / Math.sqrt(1.0125)), f(-0.35 / Math.sqrt(1.0125))];
  const sphereColours = [[6, 112, 109], [62, 234, 140], [217, 154, 60]];
  const floorA = dark ? [40, 46, 50] : [226, 231, 233], floorB = dark ? [27, 32, 35] : [243, 245, 246];
  const sky0 = dark ? [21, 25, 28] : [255, 255, 255], sky1 = dark ? [44, 58, 64] : [236, 241, 242];
  const light = dark ? f(0.9) : 1;
  /**
   * |p - c| - r for sphere k, as SPHERE_DIST computes it.
   * @param {number} k the sphere (0..2).
   * @param {number} px the point's x (f32).
   * @param {number} py its y.
   * @param {number} pz its z.
   * @returns {number} the distance.
   */
  const sphere = (k, px, py, pz) => {
    const dx = f(px - S[k][0]), dy = f(py - S[k][1]), dz = f(pz - S[k][2]);
    return f(sqrt(f(f(f(dx * dx) + f(dy * dy)) + f(dz * dz))) - S[k][3]);
  };
  const scale = f(f(1.2) / H);
  return (x, y) => {
    const v = f(f(y - f(H * 0.5)) * scale), u = f(f(x - f(W * 0.5)) * scale);
    let dx = f(f(fx + f(v * ux)) + f(u * rx)), dy = f(fy + f(v * uy)), dz = f(f(fz + f(v * uz)) + f(u * rz));
    const dl = sqrt(f(f(f(dx * dx) + f(dy * dy)) + f(dz * dz)));
    dx = f(dx / dl); dy = f(dy / dl); dz = f(dz / dl);
    let t = 0, mat = -1;
    for (let i = 0; i < 96; i++) {
      const px = f(camx + f(dx * t)), py = f(camy + f(dy * t)), pz = f(camz + f(dz * t));
      let d = py, m = 3;
      for (let k = 0; k < 3; k++) { const e = sphere(k, px, py, pz); if (e < d) { d = e; m = k; } }
      const hit = d < f(t * f(0.001));
      if (hit) mat = m;
      if (hit || t > 40) break;
      t = f(t + d);
    }
    let px = f(camx + f(dx * t)), py = f(camy + f(dy * t)), pz = f(camz + f(dz * t));
    const isPlane = mat === 3, c = mat === 0 ? S[0] : mat === 1 ? S[1] : S[2];
    const nx = isPlane ? 0 : f(f(px - c[0]) / c[3]), ny = isPlane ? 1 : f(f(py - c[1]) / c[3]), nz = isPlane ? 0 : f(f(pz - c[2]) / c[3]);
    const dif = Math.max(0, f(f(f(nx * L[0]) + f(ny * L[1])) + f(nz * L[2])));
    px = f(px + f(nx * f(0.01))); py = f(py + f(ny * f(0.01))); pz = f(pz + f(nz * f(0.01)));
    let sh = 1, st = f(0.02), active = mat >= 0 && (dif > 0 || isPlane);
    for (let i = 0; i < 40 && active; i++) {
      const qx = f(px + f(L[0] * st)), qy = f(py + f(L[1] * st)), qz = f(pz + f(L[2] * st));
      let h = qy;
      for (let k = 0; k < 3; k++) h = Math.min(h, mat === k ? 1000 : sphere(k, qx, qy, qz));
      if (h < f(0.001)) { sh = 0; active = false; break; }
      sh = Math.min(sh, f(f(10 * h) / st));
      st = f(st + h);
      active = st < 12;
    }
    const l = f((isPlane ? f(f(0.55) + f(f(0.45) * sh)) : f(f(0.3) + f(f(f(0.7) * dif) * sh))) * light);
    const checker = ((Math.trunc(Math.floor(px)) + Math.trunc(Math.floor(pz))) & 1) === 1;
    const k = Math.min(Math.max(f(dy * 2), 0), 1);
    let word = 0xFF000000;
    for (let ch = 0; ch < 3; ch++) {
      const base = isPlane ? (checker ? floorA[ch] : floorB[ch]) : sphereColours[mat === 0 ? 0 : mat === 1 ? 1 : 2][ch];
      const out = mat >= 0 ? f(base * l) : f(sky0[ch] + f(f(sky1[ch] - sky0[ch]) * k));
      word |= Math.trunc(out) << (8 * ch);
    }
    return word >>> 0;
  };
}

/**
 * The ray kernel: its passes and their work, the finished picture against the reference (every
 * fourth row, every pixel of it), its coarse first pass, the orbit, the zoom and the themes.
 * @param {string} path ray.wasm.
 */
async function checkRay(path) {
  const m = await load(path);
  const W = 960, H = 540;
  const frame = m.demo_init(W, H);
  check(frame > 0 && m.demo_init(944, 540) === 0, 'ray: demo_init takes 960 x 540, refuses a width that is not a multiple of 32');
  m.demo_init(W, H);
  /**
   * A frame pixel as a little-endian RGBA word.
   * @param {number} x the column.
   * @param {number} y the row.
   * @returns {number} the word.
   */
  const px = (x, y) => new DataView(m.memory.buffer).getUint32(frame + (y * W + x) * 4, true);
  // The first frame's share (about 16,000 rays) finishes the coarse pass (120 x 68 blocks of 8 x 8)
  // and begins the next (240 blocks a row at 4 x 4): below the rows it reached, the coarse
  // blocks are still there, each one colour.
  const first = m.demo_frame();
  const refinedRows = Math.ceil((first - 8160) / 240) * 4;
  let uniform = true;
  for (let by = Math.ceil(refinedRows / 8); by < 67; by++) for (let bx = 0; bx < 120; bx++) {
    const c = px(bx * 8, by * 8);
    for (let k = 1; k < 8; k++) uniform = uniform && px(bx * 8 + k, by * 8 + k) === c;
  }
  check(first >= 8160 && first < 8160 + 32400 && uniform,
    `ray: the first frame draws the coarse pass (8 x 8 blocks of one colour) and starts the next (${first} rays, ${refinedRows} rows refined)`);
  let total = first, frames = 1, work;
  while ((work = m.demo_frame()) !== 0 && frames < 1000) { total += work; frames++; }
  check(total === 8160 + 32400 + 129600 + 518400, `ray: the four passes trace ${total.toLocaleString('en-US')} rays in ${frames} frames, then demo_frame returns 0`);

  const ref = rayReference(W, H, Math.fround(0.6), 6.5, false);
  let diff = 0, compared = 0;
  for (let y = 0; y < H; y += 4) for (let x = 0; x < W; x++) {
    compared++;
    if (ref(x, y) !== px(x, y)) diff++;
  }
  check(diff === 0, `ray: the picture equals the f32 reference renderer, bit for bit (${diff} of ${compared} pixels differ)`);
  check(px(480, 270) !== px(480, 10) && px(480, 10) !== px(480, 530), 'ray: sphere, sky and floor are three different colours at the centre column');
  // The light comes from the upper left: the middle sphere's upper left is its brightest teal.
  const lit = px(440, 230), shaded = px(520, 310);
  check((lit & 255) < 20 && ((lit >> 8) & 255) > 90 && ((shaded >> 8) & 255) < ((lit >> 8) & 255),
    `ray: the middle sphere is teal, lit at its upper left (0x${lit.toString(16)}) and darker at its lower right (0x${shaded.toString(16)})`);

  m.demo_pointer(400, 300, 1);
  m.demo_pointer(440, 300, 1);
  check(m.demo_frame() > 0, 'ray: a drag turns the camera and starts the passes over');
  while (m.demo_frame() !== 0);
  const turned = rayReference(W, H, Math.fround(Math.fround(0.6) + Math.fround(40 * Math.fround(0.01))), 6.5, false);
  diff = 0;
  for (let y = 0; y < H; y += 16) for (let x = 0; x < W; x += 3) if (turned(x, y) !== px(x, y)) diff++;
  check(diff === 0, `ray: after a 40-pixel drag, the picture is the reference's at angle 1.0 (${diff} differ)`);
  m.demo_wheel(480, 270, -1);
  while (m.demo_frame() !== 0);
  const nearer = rayReference(W, H, Math.fround(Math.fround(0.6) + Math.fround(40 * Math.fround(0.01))), Math.fround(6.5 * Math.fround(1 / 1.12)), false);
  diff = 0;
  for (let y = 0; y < H; y += 16) for (let x = 0; x < W; x += 3) if (nearer(x, y) !== px(x, y)) diff++;
  check(diff === 0, `ray: the wheel brings the camera 1.12 times nearer (${diff} differ)`);
  m.demo_command(1);
  m.demo_theme(1);
  while (m.demo_frame() !== 0);
  const darkRef = rayReference(W, H, Math.fround(0.6), 6.5, true);
  diff = 0;
  for (let y = 0; y < H; y += 16) for (let x = 0; x < W; x += 3) if (darkRef(x, y) !== px(x, y)) diff++;
  check(diff === 0, `ray: command 1 brings the first view back, and the dark colours equal the reference's (${diff} differ)`);
}

const [life, swarm, ray] = process.argv.slice(2);
if (life && life !== '-') await checkLife(life);
if (swarm && swarm !== '-') await checkSwarm(swarm);
if (ray && ray !== '-') await checkRay(ray);
console.log(failures ? `${failures} failed` : 'all passed');
process.exit(failures ? 1 : 0);
