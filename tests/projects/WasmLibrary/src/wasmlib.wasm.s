// wasmlib.wasm.s - the WasmLibrary test project's code, for browser-wasm and wasi-wasm (the wasm
// qualifier), in LLVM's WebAssembly assembly. asmsdk-wasm.inc comes from ImplicitUsings.
// Every function takes and returns i32 values, which C# sees as int.

// int wl_add(int a, int b): a + b.
FUNCTION wl_add, (i32, i32) -> (i32)
    local.get 0
    local.get 1
    i32.add
ENDFUNCTION wl_add

// int wl_twice(int x): 2x, through the hidden helper below, so that a call to a function
// defined further down (EXTERN_FUNCTION) and TAILCALL (a call and a return on WebAssembly) are
// both exercised.
EXTERN_FUNCTION wl_hidden, (i32) -> (i32)
FUNCTION wl_twice, (i32) -> (i32)
    local.get 0
    CALL wl_hidden
ENDFUNCTION wl_twice

// Global while linking, exported by no module: wl_add(x, x) as a tail call.
HIDDEN_FUNCTION wl_hidden, (i32) -> (i32)
    local.get 0
    local.get 0
    TAILCALL wl_add
ENDFUNCTION wl_hidden

// int wl_platform(void): the SDK's defines, summed, so one call shows which were set.
FUNCTION wl_platform, () -> (i32)
    .local i32
    i32.const 0
    local.set 0
.ifdef BROWSER
    local.get 0
    i32.const 1
    i32.add
    local.set 0
.endif
.ifdef WASI
    local.get 0
    i32.const 2
    i32.add
    local.set 0
.endif
.ifdef RELEASE
    local.get 0
    i32.const 10
    i32.add
    local.set 0
.endif
.ifdef FREESTANDING
    local.get 0
    i32.const 100
    i32.add
    local.set 0
.endif
    local.get 0
ENDFUNCTION wl_platform

// int wl_sum(const int *values, int count): a loop over linear memory. Locals: 0 values,
// 1 count, 2 the sum. The loop runs while count > 0.
FUNCTION wl_sum, (i32, i32) -> (i32)
    .local i32
    block
    loop
        local.get 1
        i32.eqz
        br_if 1
        local.get 2
        local.get 0
        i32.load 0
        i32.add
        local.set 2
        local.get 0
        i32.const 4
        i32.add
        local.set 0
        local.get 1
        i32.const 1
        i32.sub
        local.set 1
        br 0
    end_loop
    end_block
    local.get 2
ENDFUNCTION wl_sum

// int wl_table_sum(void): the module's own table (read-only data), through wl_sum.
FUNCTION wl_table_sum, () -> (i32)
    ADDRESS wl_table
    i32.const 4
    CALL wl_sum
ENDFUNCTION wl_table_sum

// int wl_simd(int x): x + x in the first lane of two i32x4 vectors.
FUNCTION wl_simd, (i32) -> (i32)
    local.get 0
    i32x4.splat
    local.get 0
    i32x4.splat
    i32x4.add
    i32x4.extract_lane 0
ENDFUNCTION wl_simd

// int wl_square(int x): x * x, through ws_mul of the WasmStatic static library this library
// links (its type declared first: llvm-mc needs it before the call).
EXTERN_FUNCTION ws_mul, (i32, i32) -> (i32)
FUNCTION wl_square, (i32) -> (i32)
    local.get 0
    local.get 0
    CALL ws_mul
ENDFUNCTION wl_square

// File-local: neither exported nor callable from another file.
LOCAL_FUNCTION wl_local, () -> (i32)
    i32.const 7
ENDFUNCTION wl_local

    RODATA
// 1 + 2 + 3 + 36 = 42. A data label needs its size (ENDDATA); .Lend, a .L label, needs none.
// .int takes a list; the wasm parser's own .int32 takes a single value. Aligned to 4, as the
// i32.load in wl_sum reads it (llvm-mc aligns no data by itself).
    .p2align 2
wl_table:
    .int 1, 2, 3, 36
.Lend:
ENDDATA wl_table
