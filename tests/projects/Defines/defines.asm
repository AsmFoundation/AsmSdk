; =============================================================================
; defines.asm - the Defines test project. Assembles only when the SDK passed the
; expected symbols; each failed expectation stops NASM with its own message.
; Built in Release, for the freestanding target framework.
; =============================================================================

%ifndef RELEASE
%error "RELEASE is not defined: the configuration symbol was lost when the project set DefineConstants"
%endif

%ifndef FREESTANDING
%error "FREESTANDING is not defined: the target framework symbol is missing"
%endif

%ifdef LIBC
%error "LIBC is defined in a freestanding build"
%endif

%ifdef NETSTANDARD
%error "NETSTANDARD reached NASM: the managed SDK's implicit framework defines were not hidden"
%endif

%ifndef ANSWER
%error "ANSWER is not defined: the project's DefineConstants did not reach NASM"
%elif ANSWER != 42
%error "ANSWER is not 42: NAME=VALUE entries lost their value"
%endif

default rel
bits 64

section .text

; -----------------------------------------------------------------------------
; defines_answer - returns ANSWER, so the object has code for the DWARF line
; table that DebugSymbols=true must produce.
; @returns eax = ANSWER (42)
; -----------------------------------------------------------------------------
global defines_answer:function
defines_answer:
    mov     eax, ANSWER
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
