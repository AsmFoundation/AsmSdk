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

Two of the [examples](../examples/index.md) show it in full: Crc32C builds its library for both
wasm RIDs and `Crc32C.Browser` calls it from a .NET WebAssembly app
([In WebAssembly](../examples/crc32c.md#in-webassembly)), and HexDump is also a WASI command
([The WASI build](../examples/hexdump.md#the-wasi-build)).

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

Two `dotnet new` templates start the same way (see [Projects](projects.md)):

```sh
dotnet new asmwasm -n Calc        # this library, with a run.mjs that calls it from Node
dotnet build Calc
node Calc/run.mjs                 # add_ints(40, 2) = 42

dotnet new asmwasmapp -n Calc     # a .NET browser-wasm app and the library it calls
dotnet workload install wasm-tools
dotnet run --project Calc/Calc.App
```


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


### PROTO: the C# bindings

A `PROTO` line before a `FUNCTION` declares the function's C signature for the generated C#
bindings, as in NASM and GNU sources (see
[Writing NASM](writing-nasm.md#proto-a-functions-c-prototype)):

```asm
PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add_ints
```

- **The records.** `PROTO` and every function macro write a text record into the object's
  `asmapi` custom section, the WebAssembly form of the `.asmapi` section native objects carry.
  The SDK reads them after assembling each RID and writes the RID's API file, from which a
  .NET WebAssembly app's bindings come (see [the app](#in-a-net-webassembly-app)).
- **An exact check.** A `FUNCTION`'s record carries its whole wasm type, so `PROTO` must lower to
  exactly that type, or the build stops with **ASM0132**. Native code can compare only an
  argument count. The lowering is clang's for `wasm32`:

  | C type | wasm type |
  |---|---|
  | `bool`, `char`, the integers of 32 bits or fewer, `size_t`, `intptr_t`, any pointer, a function pointer | `i32` |
  | `int64_t`, `uint64_t` | `i64` |
  | `float` | `f32` |
  | `double` | `f64` |
  | `void` (a return) | no result |

- **One API for every RID.** A library for `linux-x64` and `browser-wasm` declares the same `PROTO`
  lines in its `.asm` and its `.wasm.s`, or the build stops with **ASM0133**, as it does between
  Windows and Linux. A C# library shared by a desktop app and a Blazor app then sees one API.
- **The module keeps them.** wasm-ld keeps the `asmapi` section in the module it links, even with
  `--strip-all` (measured), as a Linux `.so` keeps `.asmapi`. A JavaScript host can read the API
  there:

  ```js
  const records = WebAssembly.Module.customSections(module, "asmapi")
    .flatMap((s) => new TextDecoder().decode(s).split("\0").filter((r) => r));
  // [ "proto int32_t add_ints(int32_t a, int32_t b)", "fn 1 add_ints (i32, i32) -> (i32)" ]
  ```

  Each function costs about 30 bytes, plus its PROTO line's text.
- **Between functions.** `PROTO` goes before its `FUNCTION`, never inside a function body.
  llvm-mc's wasm parser cannot return to the section a record leaves it in (it has no
  `.pushsection`), and a record written inside a function body took the rest of the body with it
  (measured: the module linked, and the function had lost its `end`). So `FUNCTION` arms a guard
  that `ENDFUNCTION` disarms, and a `PROTO`, or a `FUNCTION`, in between stops the build: "PROTO
  and the function macros go between functions: f has no ENDFUNCTION yet". llvm-mc reports a
  macro's `.error` at `<instantiation>`. The note after it, "while in macro instantiation", names
  the source line.

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

- `browser-wasm`: the `wasm-tools` workload (`dotnet workload install wasm-tools`) of the SDK of
  the app's own .NET. A later SDK names the earlier .NET's workload with a suffix
  (`wasm-tools-net10` on SDK 11), and whether that works depends on how the app is made
  (measured with SDK 11.0.100 and `wasm-tools-net10`):
  - an app made with **`Microsoft.NET.Sdk.WebAssembly`** (the `wasmbrowser` template, Blazor
    WebAssembly) built and ran, asm code included: a later SDK with `wasm-tools-net10` works;
  - an app made with **`Microsoft.NET.Sdk` and `RuntimeIdentifier` `browser-wasm`** (the
    `wasmconsole` template, the `asmwasmapp` template, `Crc32C.Browser`) did not build: .NET's
    own targets stopped every `net10.0` one, with asm code or without ("WasmAssembliesToBundle
    item is empty" on Windows 11, "Could not find assembly 'System.Private.CoreLib'" on GitHub's
    Windows image). Build it with the SDK of its own .NET: for a `net10.0` app, a .NET 10 SDK
    (pin it in `global.json`).
- `wasi-wasm`: the `wasi-experimental` workload, and the WASI SDK in `WASI_SDK_PATH`. .NET 10
  needs WASI SDK 25.0 exactly. Build a WASI app with the SDK of its own .NET (pin it in
  `global.json`): .NET 11.0.100's WASI build gives a `net10.0` app no native build, whatever
  is installed. Run the app with `wasmtime run -S http`, because .NET's WASI runtime imports
  WASI HTTP.

Without that native build, .NET builds the app as plain IL. It never links the asm code, and every
call into the code would fail once the app runs. So an app that links asm projects, or an asm
package, stops with **ASM0054**, which names the fix for the app's shape: on a later SDK,
`dotnet workload install wasm-tools-net10` (or the app's own SDK) for a
`Microsoft.NET.Sdk.WebAssembly` app, and the app's own SDK for a `Microsoft.NET.Sdk` one, with
the reason.

**Generated bindings.** When the asm sources carry `PROTO` lines (see
[PROTO](#proto-the-c-bindings)), the app needs no `[LibraryImport]` at all: the asm project's
WebAssembly build writes its API file, and the SDK writes the library's baseline from it (the
generator does once the app's code names `[AsmLibrary]`), as for a native app (see
[Interop](interop.md#generated-bindings)):

```csharp
int sum = Calc.Add.AddInts(40, 2);   // the namespace Calc (the library), its source add.wasm.s, add_ints
```

An asm library can also reach the app through a .NET library that references it, as
`samples/Crc32C.Browser` reaches `Crc32C` through `Crc32C.Net`: it is linked into the app's runtime
the same way, and nothing is laid out or copied, since a WebAssembly app deploys no native file.

`dotnet run` of a `browser-wasm` console app runs it in Node through .NET's `WasmAppHost`, which
needs a `runtimeconfig.template.json` naming the host, as .NET's `wasmconsole` template has one.
Without it, `dotnet run` stops with "no perHostConfigs found". The `asmwasmapp` template
carries one:

```json
{ "wasmHostProperties": { "perHostConfig": [ { "name": "node", "js-path": "main.mjs", "host": "nodejs" } ] } }
```

An asm *program* (`Exe`) is not delivered to a WebAssembly app: nothing there could start it.

**A Release build runs without .NET's debugger.** .NET writes `debugLevel: -1` into the boot
configuration of every browser app *build*, Release too, and in Chromium and Firefox its runtime
then starts with the debugger on: the interpreter keeps every method unoptimized and the
jiterpreter compiles no hot loop. Measured with `samples/Crc32C.Browser` in headless Chrome: its
C# ran at 5 to 7 MB/s with the debugger on (0 traces compiled) and 134 MB/s without. Node never
turns the debugger on, so the same bundle is fast there. So AsmSdk sets `WasmDebugLevel` for the
build of a browser app that links asm code, through project references and packages alike:

| Build | `WasmDebugLevel` | What the runtime does |
|---|---|---|
| Release | `0` | no debugger; the interpreter optimizes and the jiterpreter compiles |
| Debug | `-1` | the debugger, without its logging (.NET's own value for a build) |
| another configuration | .NET's | `-1` |
| any publish | .NET's | `0`, and the debugger is linked out of the runtime |

A value the app sets itself, in its project, a `Directory.Build.props` or on the command line,
wins. With the default in place, `Crc32C.Browser`'s C# ran at 128 to 158 MB/s in a Release
build in Chrome, against 11.6 to 12.2 MB/s with `-p:WasmDebugLevel=-1` (unpinned runs,
2026-11-25). A build's runtime still carries .NET's debugger, as .NET's own builds do, because
.NET decides what it links before AsmSdk's targets are read: about 120 KB of
`dotnet.native.wasm`, never started at level 0. Debugging a Release build in a browser takes
`<WasmDebugLevel>-1</WasmDebugLevel>`.

**AOT happens at publish only, and a build says so.** `RunAOTCompilation=true` compiles the C#
ahead of time only when the app is published: a `dotnet build` with it gave a
`dotnet.native.wasm` byte-identical to one without, and its C# is interpreted. A build of a
browser app that links asm code, with the property set, warns **ASM0055** ("this build
interprets the C#"), so that nobody times interpreted C# believing it is AOT; time a
`dotnet publish`. A project that keeps the property for its publishes, as Blazor projects often
do, silences it with `<NoWarn>$(NoWarn);ASM0055</NoWarn>`. A publish says nothing, and neither
does a build with `RunAOTCompilationAfterBuild=true`, which compiles every untrimmed assembly
(174, about 5 minutes here, after which .NET's own `llvm-size` step failed on Windows: MSB3073).

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
- **No `STRUCT` lines yet.** A `STRUCT` line stops the build ("STRUCT is not read from
  WebAssembly sources yet"). The SDK hands native sources a struct's offsets as assembler
  constants, and llvm-mc's wasm writer refuses every constant, even a `.L` one: `i32.const .LX`
  becomes a relocation, never an immediate (measured). With one API for every RID, a library
  whose `PROTO` lines take its structs cannot build wasm RIDs yet.
- **An untyped `select` does not assemble.** llvm-mc 23.1.5's type checker refuses even a plain
  three-operand `select` ("type mismatch, expected [i32] but got [i32, i32]"). Write an `if` with a
  result:
  ```asm
  if i32
      local.get 0
  else
      i32.const 46
  end_if
  ```
- **`.set` cannot be redefined.** llvm-mc's wasm target stops with "LLVM ERROR: .set redefinition
  is not supported", even for a `.L` name, so a macro cannot keep a counter or a flag in a
  symbol. A label (defined once) and a macro that is purged and defined again can stand in, as
  the PROTO guard does.

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
| `ASM0131` | A `PROTO` line for no function this image offers: no `FUNCTION` of that name, a `LOCAL_FUNCTION`, or a `HIDDEN_FUNCTION` of a module. | Declare the function with `FUNCTION`. |
| `ASM0132` | A `PROTO` line that lowers to another wasm type than its `FUNCTION` declares (`int64_t` is `i64`, `double` is `f64`). | Make them agree. |
| `ASM0133` | The library's RIDs declare a function differently, WebAssembly included: one `PROTO` line in `add.asm` and none in `add.wasm.s`. | Declare it the same way in every RID's source. |
| `ASM0054` | A .NET WebAssembly app links asm projects or an asm package, and .NET has no native WebAssembly build for the app's .NET version: the app would build without the asm code. | The workload the message names (`wasm-tools`, `wasi-experimental`) on the SDK of the app's own .NET. On a later SDK: `wasm-tools-net10` for a `Microsoft.NET.Sdk.WebAssembly` app; the app's own SDK in `global.json` for a `Microsoft.NET.Sdk` browser app or a WASI app. |
| `ASM0055` (warning) | A build of a browser app that links asm code sets `RunAOTCompilation`: .NET compiles ahead of time only when it publishes, so this build interprets the C#. | Time a `dotnet publish`; `<NoWarn>$(NoWarn);ASM0055</NoWarn>` when the property is there for the publishes. |
| `ASM0005` | `AsmToolchain=System` without `AsmLinkerCommand` for a linked wasm output. | `AsmLinkerCommand=wasm-ld`, with `AsmArchiverCommand=llvm-ar`. |
