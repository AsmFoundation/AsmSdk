// Calls add_ints, a function written in WebAssembly assembly in AsmWasmApp.1.Native and linked
// into this app's runtime, and prints its answer. AsmWasmApp._1.Native.Add is the C# binding the
// SDK writes from the PROTO line in add.wasm.s: the namespace of the library, the class of the
// file add.wasm.s, the method of add_ints.
int sum = AsmWasmApp._1.Native.Add.AddInts(40, 2);
Console.WriteLine($"add_ints(40, 2) = {sum}");
