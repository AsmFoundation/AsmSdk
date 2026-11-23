// =============================================================================
// asmsdk-gnu-implicit.inc - what ImplicitUsings puts in front of every
// GNU-syntax source (.s, assembled by llvm-mc) of an .asmproj, the counterpart
// of asmsdk-implicit.inc for NASM.
//
// llvm-mc has no pre-include option, so when ImplicitUsings is enable or true
// (the default) AsmFoundation.Sdk assembles a small file it generates in obj/
// for each source:
//
//   .include "asmsdk-gnu-implicit.inc"
//   .include "<the source>"
//
// Diagnostics still name the source and its own line numbers: llvm-mc reports
// an error in an included file at that file's line.
//
// What it adds:
//   - asmsdk-gnu.inc, the platform layer (FUNCTION, ENDFUNCTION, RODATA,
//     ADDRESS, CALL, TAILCALL, ...).
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
