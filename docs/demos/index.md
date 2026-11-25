# Wasm Demos

Three programs written by hand in WebAssembly's assembly language and built with AsmSdk, running
in this page. They use one core and no GPU, and you install nothing.

> [!NOTE]
> **Not released yet:** WebAssembly is on AsmSdk's main branch only (see the
> [changelog](../guide/changelog.md)). These modules were built by it, from
> [`samples/WasmDemos`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/WasmDemos).

| Demo | What it does | Module |
|---|---|---|
| [Life, 16 million cells](life.md) | Conway's Life on a 4096 by 4096 board, 128 cells per instruction | `life.wasm`, 3.3 KB |
| [Ray-marched scene](ray.md) | spheres and soft shadows, four rays per instruction, refining as you watch | `ray.wasm`, 4.9 KB |
| [Particle swarm](swarm.md) | 120,000 particles following your pointer, four per instruction | `swarm.wasm`, 2.5 KB |

## Only WebAssembly

Each demo is a single `.wasm.s` file. The module steps its simulation, draws every pixel of its
frame, chooses its colours and decides what the pointer does. Sine and cosine are written there as
polynomials, because WebAssembly has no instruction for them. The page's script,
[`demo.js`](https://github.com/AsmFoundation/AsmSdk/blob/main/docs/demos/demo.js), does only what
a browser allows nothing else to do. It starts the module, passes it the pointer, the wheel, the
buttons and the theme, and copies its frame to the canvas.

All three modules share one interface, so the same script starts any of them:

| Export | What it does |
|---|---|
| `demo_init(width, height)` | sets the demo up and returns the address of its RGBA frame in the module's memory |
| `demo_theme(dark)` | chooses the light or dark colours (this site follows your system, or its theme switch) |
| `demo_pointer(x, y, buttons)`, `demo_wheel(x, y, delta)` | the pointer and the wheel, in frame pixels |
| `demo_command(command)` | the buttons under the demo |
| `demo_frame()` | one step and one drawn frame; returns the work done, for the figures |

The figures under each demo come from your browser. The script times `demo_frame` alone, on the
page's single thread, which is one core.

## How they are checked

[`check.mjs`](https://github.com/AsmFoundation/AsmSdk/blob/main/samples/WasmDemos/check.mjs) runs
each module under Node and compares it with a reference written in JavaScript:

- **Life:** three generations of the whole board match Conway's rule cell by cell, including the
  edges that wrap around. A glider crosses the corner of the board, and drawing, panning and zoom
  are checked too.
- **Particle swarm:** one step of all 120,000 particles is compared, in single precision and in
  the module's order of operations. The two agree bit for bit.
- **Ray-marched scene:** the finished picture is compared, pixel by pixel, with a reference
  renderer that repeats every operation in single precision. They agree bit for bit, after a
  camera turn, a zoom, and in both themes as well.

AsmSdk's tests run the checks on every build. They also check that the modules this site serves
are the same, byte for byte, as a fresh build of the samples.
