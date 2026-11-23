// sectionless.s - the ARM64 RIDs of Sectionless: no section directive and no include, so the
// first line below is code. llvm-mc defines such labels in .text on every format, and the
// GNU-syntax function macros switch to .text themselves; this file is here so that the program
// that links the library builds for all eight RIDs.

// int32_t sl_mul(int32_t a, int32_t b): a * b.
HIDDEN_FUNCTION sl_mul
    mul     w0, w0, w1
    ret
ENDFUNCTION sl_mul
