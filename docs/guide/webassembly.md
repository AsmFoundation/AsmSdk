# WebAssembly

> [!NOTE]
> **Not released yet:** everything on this page is on the main branch only. AsmFoundation.Sdk
> 0.3.0 on nuget.org builds the eight native RIDs and stops a `browser-wasm` or `wasi-wasm` build
> with **ASM0011**. See the [changelog](changelog.md).

WebAssembly is AsmSdk's third assembly target, after x86-64 (NASM) and ARM64 (GNU syntax). The
code is LLVM's WebAssembly assembly, the language clang emits for `wasm32` and the bundled
`llvm-mc` reads, in `.s` files qualified for WebAssembly. Two runtime identifiers build it,
under .NET's names:

| RID | Where the code runs | Library (`Library`) | Static library | Program (`Exe`) |
|---|---|---|---|---|
| `browser-wasm` | the browser, Node, any JavaScript host; .NET WebAssembly apps (Blazor) | `name.wasm`, a module that exports its functions, and `name.a`, the archive .NET links | `name.a` | none: **ASM0017** |
| `wasi-wasm` | wasmtime and other WASI hosts; .NET's WASI apps | `name.wasm` and `name.a` | `name.a` | `name.wasm`, a WASI command |

The objects are linked by the bundled `wasm-ld` (lld's WebAssembly linker), so every build
machine builds both RIDs with nothing to install. What runs the outputs:

- **A .NET WebAssembly app** links the archive into its own runtime, and C# calls it with
  `[DllImport]` or `[LibraryImport]`, as on every other platform.
- **JavaScript** (a browser, Node, Deno, Bun) instantiates the module and calls its exports.
- **A WASI host** (wasmtime, edge platforms) runs the program, or the module's exports.


## A first library

```xml
<Project Sdk="AsmFoundation.Sdk/0.3.0">
  <PropertyGroup>
    <RuntimeIdentifiers>browser-wasm;wasi-wasm</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

`add.wasm.s`:

```asm
// int add(int a, int b): the parameters are locals 0 and 1; the result is what the
// function leaves on the operand stack.
FUNCTION add, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add
```

`dotnet build` writes, for each RID, `bin/Release/freestanding/<rid>/Calc.wasm` (the module)
and `Calc.a` (the archive). The module exports `add` and its linear memory, `memory`:

```js
const { instance } = await WebAssembly.instantiateStreaming(fetch("Calc.wasm"));
instance.exports.add(40, 2);   // 42
```

WebAssembly names carry no `lib` prefix: the module and the archive are both named after
`AssemblyName`.


## Sources

A WebAssembly source is a `.s` file with a qualifier that names WebAssembly:

| Qualifier | Builds |
|---|---|
| `name.wasm.s` | `browser-wasm` and `wasi-wasm` |
| `name.browser.s`, `name.browser-wasm.s` | `browser-wasm` |
| `name.wasi.s`, `name.wasi-wasm.s` | `wasi-wasm` |

No unqualified file builds WebAssembly. An unqualified `.s` file is ARM64 code, and an `.asm`
file is x86-64 code, which `llvm-mc`'s wasm parser could never read. A project that lists a wasm
RID with no source qualified for it stops with **ASM0023**. A NASM file named for WebAssembly
(`kernel.wasm.asm`) stops with **ASM0021**. The closest file still wins, as for the other RIDs:
`io.wasm.s` serves both RIDs unless an `io.wasi.s` replaces it for WASI.

A project may mix all three languages: `crc.asm` for x64, `crc.s` for ARM64, `crc.wasm.s` for
WebAssembly, each built for its own RIDs.


## Writing WebAssembly

WebAssembly is a stack machine. An instruction takes its operands from the operand stack and
leaves its result there. A function's parameters are locals `0` to `n-1`, and `.local i32, i64`
right after `FUNCTION` adds more. Control flow is structured (`block`, `loop`, `if`, and
`br <depth>`), so there are no branch labels. Every function has a type, which the object
records, and so the platform layer's macros take a signature where the other platforms' take a
name alone.

`asmsdk-wasm.inc` is that layer. ImplicitUsings (on by default) includes it in front of every
WebAssembly source; `.include "asmsdk-gnu.inc"` in a wasm source brings it too (when `WASM`
is defined, which `DisableImplicitFrameworkDefines=true` prevents: include `asmsdk-wasm.inc`
then).

| Macro | What it writes | Notes |
|---|---|---|
| `FUNCTION name, (params) -> (results)` | `.section .text.name`, `.globl`, `.type @function`, the label, `.functype` | an exported function; its own section, which llvm-mc's wasm writer requires |
| `LOCAL_FUNCTION name, sig` | the same without `.globl` | callable from this file only |
| `HIDDEN_FUNCTION name, sig` | the same with `.hidden` | the project's other files and the archive's users call it; no module exports it |
| `ENDFUNCTION name` | `end_function` | also where llvm-mc checks that only the results are left on the stack |
| `IMPORT_FUNCTION module, name, sig` | `.functype`, `.import_module`, `.import_name` | a function the host provides: `env` for JavaScript's import object, `wasi_snapshot_preview1` for WASI |
| `EXTERN_FUNCTION name, sig` | `.functype` | a function of another file, of a linked archive, or further down this file: llvm-mc refuses a call to a function whose type it has not seen |
| `CALL name` | `call name` | |
| `TAILCALL name` | `call name`, then `return` | not `return_call` (see the footguns) |
| `RODATA`, `DATA`, `BSS` | `.section .rodata`, `.data` or `.bss`, each `,"",@` | bare `.rodata` and `.bss` do not exist in llvm-mc's wasm parser |
| `ENDDATA name` | `.size name, . - name` | every data label needs its size |
| `ADDRESS label` | `i32.const label` | a data label's address: a 32-bit offset into linear memory |

A WASI command, the way `tests/projects/WasmProgram` writes one:

```asm
IMPORT_FUNCTION wasi_snapshot_preview1, fd_write, (i32, i32, i32, i32) -> (i32)
IMPORT_FUNCTION wasi_snapshot_preview1, proc_exit, (i32) -> ()

FUNCTION _start, () -> ()
    i32.const 1          // standard output
    ADDRESS iov          // one iovec: { message, length }
    i32.const 1
    ADDRESS written      // where fd_write stores the byte count
    CALL fd_write
    drop
    i32.const 0
    CALL proc_exit
ENDFUNCTION _start

    DATA
    .p2align 2           // WASI checks the alignment of the pointers it is given
iov:
    .int message
    .int .Lmessage_end - message
ENDDATA iov
written:
    .int 0
ENDDATA written

    RODATA
message:
    .ascii "hello from WebAssembly\n"
.Lmessage_end:
ENDDATA message
```

### Defines

Every WebAssembly source sees `WASM`, `BROWSER` or `WASI`, `FREESTANDING` or `LIBC`, `DEBUG` or
`RELEASE`, and the project's `DefineConstants`, so `.ifdef WASI` chooses between WASI imports and
JavaScript ones, as `.ifdef LINUX` does in native code.

They arrive as labels rather than llvm-mc's `--defsym` constants. llvm-mc's WebAssembly object
writer stops on any constant symbol ("`NAME: absolute addressing not supported!`"), so the SDK
writes each define as a label of size 0 in a data section of its own. `.ifdef` sees a label as
it sees a defsym, but a label has no value: a define with one (`LEVEL=3`) stops the build with
**ASM0018**. Write the value in the source, or define the name alone.

### SIMD and other features

The SDK passes llvm-mc `-mattr=+simd128,+tail-call`. llvm-mc writes no `target_features`
section, so a module needs a feature at run time only if its code uses it. Together with LLVM's
default CPU, wasm sources may use:

- bulk memory (`memory.copy`, `memory.fill`);
- multivalue;
- mutable globals;
- non-trapping float-to-int;
- reference types;
- sign extension;
- SIMD128 (`v128`, `i32x4.add`, ...);
- `return_call`.

`<AsmLlvmMcFlags>-mattr=+relaxed-simd</AsmLlvmMcFlags>` adds a feature, and `-mattr=-simd128`
takes one away. .NET's own WebAssembly runtime is built with SIMD.


## Using the outputs

### In a .NET WebAssembly app

A `browser-wasm` app (Blazor WebAssembly, the `wasmbrowser` and `wasmconsole` templates) or a
`wasi-wasm` app references the asm project as any .NET project does:

```xml
<ItemGroup>
  <ProjectReference Include="../Calc/Calc.asmproj" />
</ItemGroup>
```

```csharp
[LibraryImport("Calc")]   // the asm project's AssemblyName
internal static partial int add(int a, int b);
```

The asm project is built for the app's RID. Its archive (a `Library` or `StaticLibrary`), or
its objects (`Objects`), become `NativeFileReference` items, which the app's build links into
its runtime: Emscripten links `dotnet.native.wasm` for `browser-wasm`, and the WASI SDK's clang
links the WASI app. The P/Invoke module is the asm project's `AssemblyName`, whatever the files
are called, so the same `[LibraryImport("Calc")]` that loads `Calc.dll` and `libCalc.so`
elsewhere works here. A `PackageReference` to the asm project's package works the same way,
through the package's props, with no AsmFoundation.Sdk in the app.

What the app's build needs, for the app's own .NET version:

- `browser-wasm`: the `wasm-tools` workload (`dotnet workload install wasm-tools`). A `net10.0`
  app on a later SDK needs `wasm-tools-net10` instead: a workload serves its SDK's .NET unless
  its name says otherwise.
- `wasi-wasm`: the `wasi-experimental` workload, and the WASI SDK in `WASI_SDK_PATH`. .NET 10
  needs WASI SDK 25.0 exactly. Build a WASI app with the SDK of its own .NET (pin it in
  `global.json`): .NET 11.0.100's WASI build gives a `net10.0` app no native build, whatever
  is installed. Run the app with `wasmtime run -S http`, because .NET's WASI runtime imports
  WASI HTTP.

Without that native build, .NET builds the app as plain IL. It never links the asm code, and every
call into the code would fail once the app runs. So an app that links asm projects, or an asm
package, stops with **ASM0054**, which names the fix.

An asm *program* (`Exe`) is not delivered to a WebAssembly app: nothing there could start it.

### Standalone modules

A `Library`'s module is what JavaScript, Node or a WASI host instantiates. It defines and exports
its own linear memory (`memory`) and imports exactly what `IMPORT_FUNCTION` declared:

```js
const { instance } = await WebAssembly.instantiate(bytes, {
  env: { log_i32: (value) => console.log(value) },   // IMPORT_FUNCTION env, log_i32, (i32) -> ()
});
```

Without an `ExportsFile`, a module exports every `FUNCTION` (export-dynamic), never a
`HIDDEN_FUNCTION` or a `LOCAL_FUNCTION`. With one, it exports exactly the names the file lists,
plus its memory. wasm-ld takes exact names only, so a pattern in the file is **ASM0030**.
`LinkerArg` items reach wasm-ld:

- `--initial-memory=<bytes>`;
- `-z stack-size=<bytes>` (64 KiB by default);
- `--import-memory`, for a memory the host provides;
- `--allow-undefined`, to import every undefined function from `env`.

### WASI programs

A `wasi-wasm` program (`<OutputType>Exe</OutputType>`) is a WASI command. It is entered at
`_start` (or the symbol `StartupObject` names), and it exports `_start` and its memory.
`dotnet run` starts it in wasmtime when wasmtime is on `PATH`, with the current folder as the
program's `.`, and returns its `proc_exit` code. `AsmRunIn=Wasi` asks for that way alone. On a
machine that also builds a native RID, the native build runs first, as described in
[Programs](programs.md#dotnet-run).

### In another asm project

An asm project that references a WebAssembly library links its archive, never its module:
wasm-ld cannot link against a module, because WebAssembly has no shared libraries without a
dynamic loader. `AsmProduceStaticLibrary` is therefore `true` by default for a wasm `Library`.
Set it to `false` to ship the module alone; the library then hands over its objects instead.

A library's archive holds what its module holds: its own objects, and the static libraries and
object files it links (each archive's members, added with llvm-ar's `ADDLIB`). A native shared
library contains the static libraries it linked in the same way, which is why a shared
library's references to static libraries are private by default. So a .NET app, or a package's
consumer, gets the whole library from one archive. The archive still links when the app also
links one of those static libraries itself: wasm-ld takes a member only for a symbol nothing has
defined yet (measured both ways round).


## Packages

`dotnet pack` puts each wasm RID's files under `native/`:

| Path | What |
|---|---|
| `native/<rid>/<tfm>/name.a` | the archive: a consuming asm project links it, and the package's props hand a .NET WebAssembly app of that exact RID a `NativeFileReference` |
| `native/<rid>/<tfm>/name.wasm` | a `Library`'s module, for JavaScript and WASI hosts |

Nothing goes under `runtimes/`. A .NET app loads nothing from there on WebAssembly, and NuGet
would copy the module into every app's output.


## Footguns

Each was measured with the bundled LLVM 23.1.5 and .NET 10.0.15:

- **No assembler constants.** `.set NAME, 42`, `.equ` and `NAME = 42` stop llvm-mc's wasm writer
  with a crash and "`NAME: absolute addressing not supported!`". The SDK reports it as
  **ASM0019**, naming the source. Write the number itself, or a macro:
  ```asm
  .macro BUFFER_SIZE
      i32.const 4096
  .endm
  ```
- **Every data label needs a size:** `ENDDATA name` after its last byte. Without one, the writer
  stops ("data symbols must have a size set with .size"). A `.L` label needs none.
- **A bare `.text` does nothing** in llvm-mc's wasm parser. Code after `DATA` would stay in the
  data section; the next `FUNCTION` switches by itself.
- **Data is not aligned unless you align it.** Put `.p2align 2` before 32-bit values. WebAssembly
  loads tolerate a misaligned address, at some cost in speed. WASI does not: wasmtime refuses a
  `fd_write` whose iovec is misaligned ("Pointer not aligned to 4"), though Node's WASI accepted
  it.
- **`.int32`, `.int64` take one value.** The wasm parser's own directives stop at a comma
  ("Expected EOL"). For lists write `.byte`, `.short`, `.int` and `.quad`; `.word` does not exist.
- **`TAILCALL` is a call and a return.** .NET 10's browser build runs wasm-opt over
  `dotnet.native.wasm` without the tail-call feature, and its validation refuses `return_call`
  ("return_call* requires tail calls"). Write `return_call` yourself where no .NET app links the
  code and every host has tail calls.
- **A call names a function whose type llvm-mc has seen:** `EXTERN_FUNCTION` before calling a
  function of another file, or one further down.
- **Imports are not linked.** A call to a function nobody defines is wasm-ld's
  "undefined symbol" unless `IMPORT_FUNCTION` declared it. The host then provides it at
  instantiation.
- **`libc` builds no module.** The bundled wasm-ld links no C library, so a `libc` `Library` or
  `Exe` for WebAssembly is **ASM0016**. A `libc` `StaticLibrary` builds: the .NET app that links
  it brings the C library (Emscripten's, or wasi-libc).


## Diagnostics

| Code | Meaning | Fix |
|---|---|---|
| `ASM0016` | A `libc` module or WASI program. | Build it `freestanding`, or make it a `StaticLibrary` for .NET apps. |
| `ASM0017` | A program (`Exe`) for `browser-wasm`. | A `Library` for the browser, or the program for `wasi-wasm`. |
| `ASM0018` | A define with a value, or a define that is not a symbol name, in a WebAssembly build. | Define the name alone. |
| `ASM0019` | llvm-mc's wasm object writer stopped on the source, with its "LLVM ERROR" line. | Usually a `.set` constant, a data label without `ENDDATA`, or a function outside its own section. |
| `ASM0021` | A `.asm` file qualified for WebAssembly. | WebAssembly goes in `.s` files. |
| `ASM0023` | A wasm RID with no source qualified for it. | Add `name.wasm.s`, or remove the RID. |
| `ASM0030` | A pattern in a module's `ExportsFile`. | List exact names. |
| `ASM0054` | A .NET WebAssembly app links asm projects or an asm package, and .NET has no native WebAssembly build for the app's .NET version: the app would build without the asm code. | The workload the message names (`wasm-tools`, `wasi-experimental`, or `wasm-tools-net10` for a `net10.0` app on a later SDK), or the SDK of the app's own .NET. |
| `ASM0005` | `AsmToolchain=System` without `AsmLinkerCommand` for a linked wasm output. | `AsmLinkerCommand=wasm-ld`, with `AsmArchiverCommand=llvm-ar`. |
