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
;   - On every OS, "section .text", the section a source without a section
;     directive expects its code in. NASM's COFF output (Windows) writes a
;     label defined before the first section directive as an undefined
;     symbol, although the label's code does go to .text (measured with the
;     official NASM 2.16.03, 3.01 and 3.02 binaries; ELF and Mach-O are not
;     affected). Without this line, a HIDDEN_FUNCTION of a static library, a
;     program's main, or a FUNCTION of a project with an ExportsFile, written
;     with no section directive, could not be linked on Windows ("undefined
;     symbol"). A FUNCTION in any other DLL escaped only because its export
;     directive switches back with __SECT__, which NASM defines as
;     "[section .text]" until a source names a section.
;
; A source that still writes any of this itself is fine: asmsdk.inc has an
; include guard, "default rel" may be repeated, and declaring the note or
; .text again with the same attributes changes nothing. A file that wants
; absolute addressing writes "default abs" after this, which wins.
;
; Turn it off with <ImplicitUsings>disable</ImplicitUsings> in the project:
; for raw NASM, or for a codebase whose own macros share a name with
; asmsdk.inc's. Assembled outside AsmSdk, include it (or asmsdk.inc) yourself.
; =============================================================================

%include "asmsdk.inc"

default rel

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif

; Every OS, last: the source starts in .text, named by a section directive.
; On Linux this returns from the note above. On Windows the directive itself
; is the point: NASM's COFF output defines a label only in a section some
; directive named, and writes one in its implicit default section as an
; undefined symbol (see the header). macOS gets it for one rule everywhere.
section .text
