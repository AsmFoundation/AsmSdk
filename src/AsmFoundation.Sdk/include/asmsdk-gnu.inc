// =============================================================================
// asmsdk-gnu.inc - AsmFoundation.Sdk's platform layer for GNU-syntax assembly,
// the .s files that llvm-mc assembles: the language of the ARM64 RIDs (and of
// x86-64 sources that say so with an x64 qualifier, in AT&T syntax).
//
//   .include "asmsdk-gnu.inc"          // the SDK puts it on every include path
//
//   FUNCTION crc32c_append             // exported, in .text, aligned
//       stp     x29, x30, [sp, #-16]!  // AAPCS64 everywhere: x0-x7 in, x0 out
//       mov     x29, sp
//       CALL    crc32c_table_lookup    // bl with this OS's name for it
//       ldp     x29, x30, [sp], #16
//       ret
//   ENDFUNCTION crc32c_append
//
// On ARM64 the three OSes share the calling convention (AAPCS64: integer
// arguments in x0-x7, the result in x0, x19-x28 and d8-d15 callee-saved), so
// unlike asmsdk.inc for x86-64 there are no argument macros. What differs is
// how a symbol is named and declared, how a DLL exports it, and how an
// address is formed:
//
//                  Linux (ELF)          Windows (COFF)         macOS (Mach-O)
//   C name         name                 name                   _name
//   function type  .type name,%function .def name ... .endef   (none)
//   exported       every global symbol  -export:name directive every global
//   address        adrp + :lo12:        adrp + :lo12:          @PAGE, @PAGEOFF
//
// FUNCTION name        Starts an exported function: global, in .text, aligned
//                      (4 bytes on ARM64, 16 on x86-64), named for the OS. On
//                      Windows it also writes an "-export:name" directive in
//                      .drectve (__declspec(dllexport)), unless the project has
//                      an ExportsFile, which the SDK signals with
//                      ASMSDK_EXPORTS_FILE: the file decides the exports then.
// LOCAL_FUNCTION name  The same, not global (callable from this file only).
// ENDFUNCTION name     Closes it (ELF records the function's size).
// CALL name            Calls a function by its C name: bl (ARM64), or call
//                      (x86-64; through the PLT on Linux). macOS gets the
//                      underscore. The linker routes a call into a shared
//                      library through its stub, so the same line serves a
//                      function of this module, of a static library, or of
//                      the C library.
// TAILCALL name        The same as a jump: b (ARM64) or jmp (x86-64).
// RODATA               Switches to read-only data, whose section each format
//                      names differently: .rodata (ELF), .rdata (COFF),
//                      __TEXT,__const (Mach-O, where ".section .rodata" is an
//                      error). .data and .bss are the same on all three.
// ADDRESS reg, label   Puts the address of a label of THIS file (a data table,
//                      or a function declared with FUNCTION or LOCAL_FUNCTION)
//                      in reg, position-independently: adrp + add on ARM64,
//                      lea label(%rip) on x86-64. On macOS, FUNCTION and
//                      LOCAL_FUNCTION also define the plain name as a local
//                      label next to _name, so that one spelling serves.
//
// The SDK defines WINDOWS, LINUX or MACOS (and MUSL), X64 or ARM64,
// FREESTANDING or LIBC, DEBUG or RELEASE, each as 1 with llvm-mc's --defsym,
// so ".ifdef MACOS" works as "%ifdef MACOS" does in NASM. Assembled outside
// AsmSdk, define one OS and one CPU yourself (--defsym LINUX=1
// --defsym ARM64=1); without them this file assumes Linux on ARM64.
//
// Footguns:
//   - x18 is the platform register on Windows (the TEB) and macOS: never use
//     it, even though Linux would allow it.
//   - sp must stay 16-byte aligned whenever it is used to access memory
//     (AArch64 faults otherwise), and a function that calls another saves
//     x29 and x30 first.
//   - The names here are case-sensitive and upper case (llvm-mc macros are
//     case-sensitive): "call" is the x86-64 instruction, "CALL" this macro.
//   - ADDRESS takes labels of this file. (The SDK links Linux shared
//     libraries -Bsymbolic, so even a global symbol of the library binds to
//     the library itself; with -Bno-symbolic, ld.lld refuses an adrp to a
//     global symbol: "recompile with -fPIC".)
//   - Non-leaf functions carry no unwind information on Windows (no .pdata):
//     a debugger or a crash dump cannot walk the stack through them.
//   - llvm-mc keeps --defsym names as local absolute symbols in each object
//     (LINUX, ARM64, ...): harmless, but they show in the symbol table.
// =============================================================================

.ifndef ASMSDK_GNU_INC
.set ASMSDK_GNU_INC, 1

// ---- Alignment of a function's first instruction ----------------------------
// ARM64 instructions are 4 bytes and must be 4-byte aligned; x86-64 functions
// start on 16 bytes, the fetch block size, as compilers place them.
.macro ASMSDK_ALIGN_FUNCTION
.ifdef X64
    .p2align 4
.else
    .p2align 2
.endif
.endm

// ---- Exports on Windows -------------------------------------------------------
// The directive lld-link reads from .drectve, then back to .text: the include
// puts every function there, so returning to it needs no saved section.
.macro ASMSDK_EXPORT name
.ifdef WINDOWS
.ifndef ASMSDK_EXPORTS_FILE
    .section .drectve,"yn"
    .ascii " -export:\name"
.endif
.endif
    .text
.endm

// ---- Functions -----------------------------------------------------------------
.macro FUNCTION name
    ASMSDK_EXPORT \name
.ifdef MACOS
    .globl _\name
    ASMSDK_ALIGN_FUNCTION
_\name:
    // The plain name too, local, at the same address: ADDRESS takes it.
\name:
.else
    .globl \name
.ifdef WINDOWS
    // COFF function symbol: storage class external (2), type 0x20 (function).
    .def \name
    .scl 2
    .type 32
    .endef
.else
    .type \name, %function
.endif
    ASMSDK_ALIGN_FUNCTION
\name:
.endif
.endm

.macro LOCAL_FUNCTION name
    .text
.ifdef MACOS
    // The underscore here too, so that CALL reaches local and global
    // functions alike, and the plain name for ADDRESS.
    ASMSDK_ALIGN_FUNCTION
_\name:
\name:
.else
.ifdef WINDOWS
    .def \name
    .scl 3
    .type 32
    .endef
.else
    .type \name, %function
.endif
    ASMSDK_ALIGN_FUNCTION
\name:
.endif
.endm

.macro ENDFUNCTION name
.ifndef WINDOWS
.ifndef MACOS
    .size \name, . - \name
.endif
.endif
.endm

// ---- Sections ----------------------------------------------------------------------
.macro RODATA
.ifdef MACOS
    .section __TEXT,__const
.else
.ifdef WINDOWS
    // "dr": initialized data, read-only, as MSVC's .rdata.
    .section .rdata,"dr"
.else
    .section .rodata
.endif
.endif
.endm

// ---- Calls and addresses ---------------------------------------------------------
.macro CALL name
.ifdef X64
.ifdef MACOS
    call _\name
.else
.ifdef WINDOWS
    call \name
.else
    call \name@PLT
.endif
.endif
.else
.ifdef MACOS
    bl _\name
.else
    bl \name
.endif
.endif
.endm

.macro TAILCALL name
.ifdef X64
.ifdef MACOS
    jmp _\name
.else
.ifdef WINDOWS
    jmp \name
.else
    jmp \name@PLT
.endif
.endif
.else
.ifdef MACOS
    b _\name
.else
    b \name
.endif
.endif
.endm

.macro ADDRESS reg, label
.ifdef X64
    leaq \label(%rip), \reg
.else
.ifdef MACOS
    adrp \reg, \label@PAGE
    add \reg, \reg, \label@PAGEOFF
.else
    adrp \reg, \label
    add \reg, \reg, :lo12:\label
.endif
.endif
.endm

.endif
