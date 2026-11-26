# Life, 16 million cells

Conway's Life on a board of 4096 by 4096 cells whose edges wrap around. Each WebAssembly
instruction updates 128 cells at once.

<div class="wasm-demo" data-module="life.wasm" data-unit="cells" data-wheel>
  <canvas width="960" height="540" class="wasm-demo-pixels" aria-label="Conway's Life on a 4096 by 4096 board, running"></canvas>
  <div class="wasm-demo-bar">
    <span class="wasm-demo-numbers">Starting the module...</span>
    <span class="wasm-demo-buttons">
      <button type="button" data-command="1">New board</button>
      <button type="button" data-command="2">Clear</button>
      <button type="button" data-command="3">Pause</button>
    </span>
  </div>
  <p class="wasm-demo-help">Drag to move around the board. Use the wheel to zoom from 8 pixels per cell out to 8 cells per pixel; zoomed out, each pixel is shaded by its share of live cells. Draw cells with the right button, for example on a cleared board.</p>
</div>
<script type="module" src="demo.js"></script>

## The kernel

The board is one bit per cell: 512 bytes per row, so one `v128.load` brings 128 neighbouring cells.
For every cell, the eight neighbours become eight bit vectors: the rows above and below, each
shifted one cell to each side, and the row itself shifted both ways. Full adders made of `xor`,
`and` and `or` then add those vectors as binary numbers whose bits are whole vectors. Life's rule
(born with 3 neighbours, survives with 2 or 3) is two more steps on the count's bits. There is no
branch per cell. One column of 128 cells is swept down the board at a time, and the partial sums
of the rows above and in the middle stay in locals, so each row is loaded once per column.

This is the step that turns the counts into the next generation:

```asm
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
```

The whole kernel, with the drawing and the controls, is
[`life.wasm.s`](https://github.com/AsmFoundation/AsmSdk/blob/main/samples/WasmDemos/Life/life.wasm.s).
Under Node on an i9-13900K, one generation of all 16,777,216 cells takes about 1.3 ms, about
13 billion cells a second on one core, and drawing the frame takes about 1 ms. The figures under
the demo are your browser's.

## Build one yourself

```sh
dotnet new asmwasm -n Kernel      # a WebAssembly library in LLVM's WebAssembly assembly
dotnet build Kernel               # Kernel/bin/Debug/freestanding/browser-wasm/Kernel.wasm
node Kernel/run.mjs               # calls it from Node; a page loads it the same way
```

The [WebAssembly guide](../guide/webassembly.md) covers the language, the `FUNCTION` macros and the
traps. The demo's project builds `browser-wasm` only and exports just the demo interface
([`Life.asmproj`](https://github.com/AsmFoundation/AsmSdk/blob/main/samples/WasmDemos/Life/Life.asmproj)).
