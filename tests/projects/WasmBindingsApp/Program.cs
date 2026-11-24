using System.Runtime.InteropServices;
using WasmBindingsApp;

// add_ints through the binding the generator wrote from WasmApi's browser-wasm API file (the PROTO
// line in add.wasm.s): the library "wasmapi" is the class Wasmapi, its source add.wasm.s the nested
// class Add (the logical file "add", without the wasm qualifier), add_ints the method AddInts.
Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier} bindings {Wasmapi.Add.AddInts(40, 2)}");
