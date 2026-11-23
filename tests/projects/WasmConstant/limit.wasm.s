// A constant symbol, the way native GNU-syntax code often names a number: fine for ELF, Mach-O
// and COFF, a fatal error for llvm-mc's wasm object writer (ASM0019).
.set LIMIT, 4096

FUNCTION limit, () -> (i32)
    i32.const 4096
ENDFUNCTION limit
