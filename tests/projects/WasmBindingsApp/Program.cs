using System.Runtime.InteropServices;

// add_ints through the baseline the SDK wrote from WasmApi's browser-wasm API file (the PROTO line
// in add.wasm.s): the library "wasmapi" is the namespace Wasmapi, its source add.wasm.s the class
// Add (the logical file "add", without the wasm qualifier), add_ints the method AddInts. This
// project never mentions the attribute, so no source generator runs.
Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier} bindings {Wasmapi.Add.AddInts(40, 2)}");
