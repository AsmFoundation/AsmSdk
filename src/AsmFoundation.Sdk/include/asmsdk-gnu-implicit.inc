// =============================================================================
// asmsdk-gnu-implicit.inc - what ImplicitUsings puts in front of every
// GNU-syntax source (.s, assembled by llvm-mc) of an .asmproj, the counterpart
// of asmsdk-implicit.inc for NASM.
//
// llvm-mc has no pre-include option, so when ImplicitUsings is enable or true
// (the default) AsmFoundation.Sdk assembles a file it generates in obj/ for
// each source:
//
//   .include "asmsdk-gnu-implicit.inc"
//   # 1 "<the source>"
//   <the source's text>
//
// Diagnostics and debug information still name the source and its own line
// numbers: the line marker says where the text came from, as in a file the C
// preprocessor wrote.
//
// What it adds:
//   - asmsdk-gnu.inc, the platform layer (FUNCTION, ENDFUNCTION, RODATA,
//     ADDRESS, CALL, TAILCALL, ...). A WebAssembly build's generated file
//     includes asmsdk-wasm.inc instead of this one (FUNCTION with a signature,
//     IMPORT_FUNCTION, ENDDATA, ...): it needs no stack note, and reaching it
//     through asmsdk-gnu.inc would depend on WASM being defined.
//   - On Linux only, the empty .note.GNU-stack section that marks the stack
//     non-executable, for the reason asmsdk-implicit.inc gives, and then back
//     to .text, where a source without a section directive puts its code.
//
// A source that still includes asmsdk-gnu.inc or declares the note itself is
// fine: the include has a guard, and the note's declaration is the same again.
// Turn it off with <ImplicitUsings>disable</ImplicitUsings> in the project.
// =============================================================================

.include "asmsdk-gnu.inc"

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
    .text
.endif
