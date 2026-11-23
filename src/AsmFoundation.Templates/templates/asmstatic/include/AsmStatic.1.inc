; AsmStatic.1.inc - the public interface of the AsmStatic.1 static library, for NASM sources.
;
; A project that references AsmStatic.1 has this folder on its include path:
;   %include "AsmStatic.1.inc"
; GNU-syntax (ARM64) sources need no declaration: llvm-mc treats a name it does not define as
; external, and the linker finds it in the library.
%ifndef ASMSTATIC1_INC_GUARD_INC
%define ASMSTATIC1_INC_GUARD_INC

; int32_t mul_ints(int32_t a, int32_t b): a * b, wrapping like C#'s unchecked multiplication.
extern mul_ints

%endif
