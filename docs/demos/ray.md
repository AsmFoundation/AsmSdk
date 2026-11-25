# Ray-marched scene

Three spheres and a checkered floor with soft shadows, drawn without the GPU. Each pixel's ray is
marched through the scene four rays at a time, and the picture sharpens while you watch.

<div class="wasm-demo" data-module="ray.wasm" data-unit="rays" data-wheel data-idle="The picture is complete.">
  <canvas width="960" height="540" aria-label="Three spheres on a checkered floor, ray-marched"></canvas>
  <div class="wasm-demo-bar">
    <span class="wasm-demo-numbers">Starting the module...</span>
    <span class="wasm-demo-buttons">
      <button type="button" data-command="1">First view</button>
    </span>
  </div>
  <p class="wasm-demo-help">Drag to turn the camera around the scene, and use the wheel to bring it nearer. Each change starts over from blocks of 8 by 8 pixels, then 4, 2 and 1.</p>
</div>
<script type="module" src="demo.js"></script>

> [!NOTE]
> **Not released yet:** built with AsmSdk's main branch, which has WebAssembly
> ([Wasm Demos](index.md)).

## The kernel

The scene is a distance field. For any point, the distance to the nearest surface is the smallest
of `|p - c| - r` over the spheres and the height above the floor. A ray advances by that distance
until it is closer than 0.001 of the way travelled (a hit), more than 40 units away (the sky), or
96 steps have passed. One `f32x4` holds the same quantity for four neighbouring rays. A ray that is
done keeps its lane through masks (`f32x4.lt`, `v128.andnot`, `v128.bitselect`) while the others
go on, and the loop ends when every lane is done (`i32x4.all_true`). A second march, towards the
light, gives the soft shadow. The camera's sine and cosine are polynomials in the module, because
WebAssembly has no instruction for them.

This is one step of the march:

```asm
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
```

The whole kernel is
[`ray.wasm.s`](https://github.com/AsmFoundation/AsmSdk/blob/main/samples/WasmDemos/Ray/ray.wasm.s).
Under Node on an i9-13900K, the four passes (688,560 rays) take about 130 ms. The figures under the
demo are your browser's, and stop once the picture is complete.

## Build one yourself

> [!NOTE]
> **Not released yet:** WebAssembly, and the `asmwasm` template, are on AsmSdk's main branch.

```sh
dotnet new asmwasm -n Kernel      # a WebAssembly library in LLVM's WebAssembly assembly
dotnet build Kernel               # Kernel/bin/Release/freestanding/browser-wasm/Kernel.wasm
node Kernel/run.mjs               # calls it from Node; a page loads it the same way
```

The [WebAssembly guide](../guide/webassembly.md) covers the language, the `FUNCTION` macros and the
traps. One trap this kernel met: `f32.const` takes a decimal only when it is short and exact
(`0.5`, `40.0`), so its other constants are hex floats, the form llvm-mc prints (`0x1.0624dep-10`
is 0.001).
