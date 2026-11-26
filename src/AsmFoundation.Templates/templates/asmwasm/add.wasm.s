// add.wasm.s - the code of AsmWasm.1, for browser-wasm and wasi-wasm.
//
// The SDK puts its WebAssembly platform layer in front of this file (ImplicitUsings, on by
// default): FUNCTION name, (params) -> (results) starts an exported function of that type, and
// ENDFUNCTION name ends it. WebAssembly is a stack machine: the parameters are locals 0, 1, ...,
// each instruction takes its operands from the operand stack, and the function returns what is
// left there. PROTO is the function's C prototype: the SDK checks that it lowers to the
// FUNCTION's wasm type, keeps it in the module's "asmapi" section, and writes the C# bindings of
// the projects that reference this library from it.
// https://asmfoundation.github.io/AsmSdk/guide/webassembly.html

// a + b, wrapping like C#'s unchecked addition.
PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add_ints
