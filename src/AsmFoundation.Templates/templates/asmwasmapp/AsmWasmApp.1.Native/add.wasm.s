// add.wasm.s - the code of AsmWasmApp.1.Native, linked into AsmWasmApp.1.App's runtime.
//
// The SDK puts its WebAssembly platform layer in front of this file (ImplicitUsings, on by
// default): FUNCTION name, (params) -> (results) starts an exported function of that type, and
// ENDFUNCTION name ends it. WebAssembly is a stack machine: the parameters are locals 0, 1, ...,
// each instruction takes its operands from the operand stack, and the function returns what is
// left there.
// https://asmfoundation.github.io/AsmSdk/guide/webassembly.html

// int32_t add_ints(int32_t a, int32_t b): a + b, wrapping like C#'s unchecked addition.
FUNCTION add_ints, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION add_ints
