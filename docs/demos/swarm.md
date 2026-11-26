# Particle swarm

120,000 particles pulled towards your pointer, four per WebAssembly instruction, or 240,000 or
500,000 if you choose. Each one adds to a fading density image, which the module colours into
the frame.

<div class="wasm-demo" data-module="swarm.wasm" data-unit="particles">
  <canvas width="960" height="540" aria-label="A particle swarm following the pointer"></canvas>
  <div class="wasm-demo-bar">
    <span class="wasm-demo-numbers">Starting the module...</span>
    <span class="wasm-demo-controls">
      <label class="wasm-demo-choice">Particles
        <select data-command>
          <option value="120000" selected>120,000</option>
          <option value="240000">240,000</option>
          <option value="500000">500,000</option>
        </select>
      </label>
      <button type="button" data-command="1">Scatter</button>
      <button type="button" data-command="3">Pause</button>
    </span>
  </div>
  <p class="wasm-demo-help">Move the pointer over the frame and the swarm follows it. Hold the button down to push it away. Without a pointer, the swarm follows a slow figure of eight. A larger count keeps the swarm and scatters the newcomers around it; a smaller one drops particles at once.</p>
</div>
<script type="module" src="demo.js"></script>

## The kernel

The particles are four arrays (x, y and the two velocities), so one `v128.load` brings the same
field of four particles and every step of the physics is one `f32x4` instruction for four of
them:

    d = attractor - p;   f = k / (|d|² + 900);   v = (v + d f) × 0.993;   p = p + v

A particle that leaves the frame turns back through lane masks, not branches. The density fades by
0.78 a frame, four pixels per `f32x4.mul`, and each pixel's density, capped at 255, picks its colour
from a 256-entry table that runs from the background through the theme's inks. The figure of eight
needs sine and cosine, which the module computes itself, by range reduction and a polynomial.

The arrays have room for 500,000 particles, and the Particles choice sets how many the loops step.
Each particle adds 18 × √(120,000 / count) to its pixel: 18 at 120,000, 12.7 at 240,000 and 8.8 at
500,000. A larger swarm draws denser, and its core still does not turn into solid ink.

This is the start of the step for four particles:

```asm
            local.get 3
            local.get 0
            v128.load 0
            local.tee 13
            f32x4.sub
            local.set 17            // dx
            local.get 4
            local.get 0
            v128.load 480000
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
```

The whole kernel is
[`swarm.wasm.s`](https://github.com/AsmFoundation/AsmSdk/blob/main/samples/WasmDemos/Swarm/swarm.wasm.s).
Under Node on an i9-13900K, colouring the frame takes about 1.5 ms whatever the count, and moving
and splatting the particles about 10 ns each. A frame takes about 2.7 ms at 120,000 particles,
3.9 ms at 240,000 and 6.3 ms at 500,000. The figures under the demo are your browser's.

## Build one yourself

```sh
dotnet new asmwasm -n Kernel      # a WebAssembly library in LLVM's WebAssembly assembly
dotnet build Kernel               # Kernel/bin/Debug/freestanding/browser-wasm/Kernel.wasm
node Kernel/run.mjs               # calls it from Node; a page loads it the same way
```

The [WebAssembly guide](../guide/webassembly.md) covers the language, the `FUNCTION` macros and the
traps.
