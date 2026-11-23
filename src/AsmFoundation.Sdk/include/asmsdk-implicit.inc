; =============================================================================
; asmsdk-implicit.inc - what ImplicitUsings puts in front of every NASM source
; of an .asmproj (AsmFoundation.Sdk passes this file to NASM as -P, a
; pre-include, when ImplicitUsings is enable or true, the default).
;
; With it, a source file is just code:
;
;   FUNCTION demo_add, 2            ; int demo_add(int a, int b)
;       lea     eax, [ARG1 + ARG2]
;       RETURN
;   ENDFUNCTION
;
; What it adds, and why each line is here rather than in every file:
;   - asmsdk.inc, the calling-convention layer (FUNCTION, ARG1..ARG6,
;     RETURN, SHADOW_SPACE, PLT, ...).
;   - "default rel": RIP-relative addressing for every memory operand without
;     a base register. Shared libraries and position-independent executables
;     on Linux and macOS cannot link absolute addresses in code, and on
;     Windows a DLL must not either (lld-link silently linked an ADDR32
;     relocation into one, wrong at run time: see ASM0080). NASM 3 also warns
;     about every operand of a file that names neither default.
;   - On Linux only, the empty .note.GNU-stack section: it tells the linker
;     the code needs no executable stack. The SDK already links with
;     -z noexecstack, but a static library (.a) shipped to C users is linked by
;     their tools, and GNU ld warns about, and then grants, an executable stack
;     for an object without the note. Declaring it here means no file forgets.
;
; A source that still writes any of this itself is fine: asmsdk.inc has an
; include guard, "default rel" may be repeated, and declaring the note again
; with the same attributes changes nothing. A file that wants absolute
; addressing writes "default abs" after this, which wins.
;
; Turn it off with <ImplicitUsings>disable</ImplicitUsings> in the project:
; for raw NASM, or for a codebase whose own macros share a name with
; asmsdk.inc's. Assembled outside AsmSdk, include it (or asmsdk.inc) yourself.
; =============================================================================

%include "asmsdk.inc"

default rel

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
; Back to NASM's default section, where a source without a section directive
; expects its code to go.
section .text
%endif
