// =============================================================================
// asmsdk-wasm.inc - AsmFoundation.Sdk's platform layer for WebAssembly: the
// .s files that llvm-mc assembles for browser-wasm and wasi-wasm, in LLVM's
// WebAssembly assembly (the syntax clang emits for wasm32 and llvm-mc reads).
//
//   .include "asmsdk-wasm.inc"   // or "asmsdk-gnu.inc", which brings this file
//                                // in a WebAssembly build; ImplicitUsings
//                                // includes it for you
//
//   IMPORT_FUNCTION env, log_i32, (i32) -> ()   // a function the host provides
//
//   FUNCTION add2, (i32, i32) -> (i32)          // exported from the module
//       local.get 0
//       local.get 1
//       i32.add
//   ENDFUNCTION add2
//
// WebAssembly is a stack machine: an instruction takes its operands from the
// operand stack and leaves its result there, a function's parameters are
// locals 0..n-1 (more with ".local i32, i64" right after FUNCTION), and control
// flow is structured (block, loop, if, br <depth>), so there are no branch
// labels. Every function has a type, which the object records: that is why the
// macros take a signature, "(params) -> (results)", where the other platform
// layers take a name alone.
//
// FUNCTION name, sig       Starts a function: in a section of its own
//                          (.text.name: llvm-mc's wasm writer holds one function
//                          per section), global, with its type. A module built
//                          without an ExportsFile exports every FUNCTION.
// LOCAL_FUNCTION name, sig The same, not global: callable from this file only.
// HIDDEN_FUNCTION name, sig The same, global but hidden: the project's other
//                          files, and the users of its archive, call it; no
//                          module exports it.
// ENDFUNCTION name         Ends it (end_function, which also checks that the
//                          operand stack holds exactly the results).
// IMPORT_FUNCTION mod, name, sig
//                          Declares a function the host imports into the module:
//                          from "env" for JavaScript (the importObject's env),
//                          from "wasi_snapshot_preview1" for WASI (fd_write,
//                          proc_exit, ...). CALL name calls it.
// EXTERN_FUNCTION name, sig
//                          Declares a function of another file, of a linked
//                          archive, or of this file further down: llvm-mc needs
//                          the type of every function a call names before the
//                          call ("symbol x: missing .functype").
// CALL name                Calls a function (call).
// TAILCALL name            Ends the function with a call whose results become
//                          this function's: a call, then return. Not the
//                          tail-call instruction (return_call), which .NET 10's
//                          browser build refuses: its wasm-opt validates
//                          dotnet.native.wasm without the tail-call feature
//                          ("return_call* requires tail calls", measured). So
//                          the frame stays until the callee returns; write
//                          return_call yourself (the SDK lets llvm-mc accept
//                          it) where every host runs tail calls (current
//                          browsers, Node, wasmtime) and nothing goes into .NET.
// RODATA, DATA, BSS        Switch to read-only data, data, or zero-filled data.
//                          Bare .rodata and .bss do not exist in llvm-mc's wasm
//                          parser, and a bare .text does nothing at all.
// ENDDATA name             Gives the data label "name" its size (.size name,
//                          . - name), which every data label must have: llvm-mc's
//                          wasm writer stops without one. Write it after the
//                          label's last byte.
// ADDRESS label            Pushes the address of a data label (i32.const label):
//                          a wasm32 pointer into the module's linear memory.
// PROTO <C prototype>      Not for WebAssembly yet: an .error. asmsdk.inc and
//                          asmsdk-gnu.inc record PROTO lines in their objects for
//                          the generated C# bindings, and the SDK reads those
//                          records from COFF, ELF and Mach-O objects only, so a
//                          wasm RID declares nothing and the library's API file
//                          describes its other RIDs. The error says so where the
//                          line is, rather than leaving it unchecked.
// STRUCT name, <field>...  Not for WebAssembly yet either, for the same reason:
//                          an .error. (The SDK lays structs out for the other
//                          RIDs only; a wasm32 layout would differ anyway, its
//                          pointers being 4 bytes.)
//
// The SDK also defines WASM, BROWSER or WASI, FREESTANDING, DEBUG or RELEASE,
// as labels (see below), so ".ifdef WASI" chooses between WASI imports and
// JavaScript ones, as ".ifdef LINUX" does in native code.
//
// Footguns:
//   - No assembler constants: ".set NAME, 42", ".equ" and "NAME = 42" stop
//     llvm-mc's wasm writer ("NAME: absolute addressing not supported!"), and
//     so would llvm-mc's --defsym, which is why the SDK's defines are labels
//     that .ifdef tests but that hold no value. Write the number itself, or a
//     macro that expands to the instruction:
//         .macro BUFFER_SIZE
//             i32.const 4096
//         .endm
//   - A data label without ENDDATA (or .size) stops the wasm writer; a .L label
//     (.Lend) needs none, because it never reaches the object.
//   - Data is not aligned unless you align it: llvm-mc places each datum right
//     after the previous one. Put .p2align 2 before 32-bit values (.p2align 3
//     for 64-bit, 4 for v128). WebAssembly loads tolerate a misaligned address,
//     slower; WASI does not: wasmtime refuses a call whose iovec or result
//     pointer is misaligned ("Pointer not aligned to 4").
//   - .int8, .int16, .int32 and .int64 are the wasm parser's own directives and
//     take ONE value ("Expected EOL" after a comma); for a list write .byte,
//     .short, .int (or .long) and .quad. .word does not exist here.
//   - A bare .text does nothing: code after RODATA/DATA/BSS stays in the data
//     section until the next FUNCTION, which switches by itself.
//   - Imports are not linked: a call to a function nobody defines is wasm-ld's
//     "undefined symbol" unless IMPORT_FUNCTION declared it. The host must then
//     provide it when it instantiates the module.
//   - Locals are typed per function (.local), and the operand stack must be
//     empty but for the results at ENDFUNCTION: llvm-mc's type checker reports
//     "type mismatch" at the line where it stops agreeing.
//   - The names here are upper case; "call" is the instruction, "CALL" the
//     macro.
//   - Assembled outside AsmSdk, include this file directly: asmsdk-gnu.inc
//     brings it only when WASM is defined, and the defsym that would define
//     it is the constant symbol llvm-mc's wasm writer refuses. Define WASM as
//     the SDK does, as a label in a data section of its own, with .size 0.
// =============================================================================

// The include guard is a .L label, a temporary symbol that never reaches the
// object: the usual ".set GUARD, 1" would be a constant symbol, which stops
// llvm-mc's wasm writer (see above).
.ifndef .Lasmsdk_wasm_inc
.Lasmsdk_wasm_inc:

// ---- Functions -------------------------------------------------------------
// The signature is the rest of the line after the name, commas and parentheses
// included (vararg): "FUNCTION add2, (i32, i32) -> (i32)". Each function gets
// its own section explicitly, whatever section the source was in: llvm-mc
// starts one by itself only when the label is in a text section, and two
// functions in one section stop the wasm writer.
.macro FUNCTION name, signature:vararg
.ifb \signature
    .error "FUNCTION \name: WebAssembly functions need their type, as in FUNCTION \name, (i32, i32) -> (i32)"
.endif
    .section .text.\name,"",@
    .globl \name
    .type \name, @function
\name:
    .functype \name \signature
.endm

.macro LOCAL_FUNCTION name, signature:vararg
.ifb \signature
    .error "LOCAL_FUNCTION \name: WebAssembly functions need their type, as in LOCAL_FUNCTION \name, (i32) -> (i32)"
.endif
    .section .text.\name,"",@
    .type \name, @function
\name:
    .functype \name \signature
.endm

// .hidden: global while linking (the project's other objects and an archive's
// users reach it), never exported by a module, export-dynamic included.
.macro HIDDEN_FUNCTION name, signature:vararg
.ifb \signature
    .error "HIDDEN_FUNCTION \name: WebAssembly functions need their type, as in HIDDEN_FUNCTION \name, (i32) -> (i32)"
.endif
    .section .text.\name,"",@
    .globl \name
    .hidden \name
    .type \name, @function
\name:
    .functype \name \signature
.endm

// end_function closes the body; a function needs no .size in a wasm object
// (its section is its extent).
.macro ENDFUNCTION name
    end_function
.endm

// ---- Declarations ----------------------------------------------------------
// An import is an undefined function with an import module and name: wasm-ld
// turns it into an entry of the module's import section, where an undefined
// function without them would be an error ("undefined symbol").
.macro IMPORT_FUNCTION module, name, signature:vararg
.ifb \signature
    .error "IMPORT_FUNCTION \module, \name: imported functions need their type, as in IMPORT_FUNCTION \module, \name, (i32) -> ()"
.endif
    .functype \name \signature
    .import_module \name, \module
    .import_name \name, \name
.endm

// The type of a function defined elsewhere (or later in this file): llvm-mc
// refuses a call to a function whose type it has not seen.
.macro EXTERN_FUNCTION name, signature:vararg
.ifb \signature
    .error "EXTERN_FUNCTION \name: declare its type, as in EXTERN_FUNCTION \name, (i32) -> (i32)"
.endif
    .functype \name \signature
.endm

// ---- Calls -----------------------------------------------------------------
.macro CALL name
    call \name
.endm

// A call and a return rather than return_call: the callee's results are on the
// operand stack, and return hands them to this function's caller, in every
// host and in .NET's WebAssembly runtime, whose browser build refuses the
// tail-call instruction (see TAILCALL above). Anything after it in the function
// is unreachable, as after a jump.
.macro TAILCALL name
    call \name
    return
.endm

// ---- Sections and data -----------------------------------------------------
// The section names wasm-ld merges into the module's data: .rodata.*, .data.*
// and .bss.* (zero-filled, stored as nothing). The empty flags string and the
// "@" are llvm-mc's spelling for a plain data section.
.macro RODATA
    .section .rodata,"",@
.endm

.macro DATA
    .section .data,"",@
.endm

.macro BSS
    .section .bss,"",@
.endm

.macro ENDDATA name
    .size \name, . - \name
.endm

// A data label's address, as wasm32 has them: a 32-bit offset into the
// module's linear memory, relocated by wasm-ld.
.macro ADDRESS label
    i32.const \label
.endm

// ---- The C# bindings' PROTO lines ------------------------------------------
// Refused, with the reason, where the line is (see the header). vararg takes the
// prototype's commas, as asmsdk-gnu.inc's PROTO does. Defined so that the line
// gets this message rather than llvm-mc's "invalid instruction".
.macro PROTO text:vararg
    .error "PROTO is not read from WebAssembly sources yet: the SDK reads the C# bindings' records from COFF, ELF and Mach-O objects only, so the library's API file describes its other RIDs. Remove the line from the wasm source."
.endm

// ---- The C# bindings' STRUCT lines -------------------------------------------
// Refused as PROTO is, where the line is (see the header). vararg takes the
// fields' commas.
.macro STRUCT name, fields:vararg
    .error "STRUCT is not read from WebAssembly sources yet: the SDK lays the C# bindings' structs out for the COFF, ELF and Mach-O RIDs only, so the library's API file describes its other RIDs. Remove the line from the wasm source."
.endm

.endif
