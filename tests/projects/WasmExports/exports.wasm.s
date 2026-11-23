// exports.wasm.s - the WasmExports test project: two exportable functions, of which the exports
// file keeps one.
FUNCTION we_exported, () -> (i32)
    i32.const 42
ENDFUNCTION we_exported

FUNCTION we_internal, () -> (i32)
    i32.const 7
ENDFUNCTION we_internal
