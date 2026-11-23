// sectionlessraw.s - the ARM64 RIDs of SectionlessRaw: ImplicitUsings is disabled, so the source
// includes asmsdk-gnu.inc itself, and names no section (llvm-mc defines such labels in .text on
// every format, and the GNU-syntax function macros switch to .text themselves). Here so that
// the program that links the library builds for all eight RIDs.
.include "asmsdk-gnu.inc"

// int32_t raw_mul(int32_t a, int32_t b): a * b.
HIDDEN_FUNCTION raw_mul
    mul     w0, w0, w1
    ret
ENDFUNCTION raw_mul
