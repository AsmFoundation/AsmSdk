; Shared.inc - the public interface of the Shared static library, for NASM sources.
;
; A project that references Shared has this folder on its include path:
;   %include "Shared.inc"
; GNU-syntax (ARM64) sources need no declaration: llvm-mc treats a name it does not define as
; external, and the linker finds it in the library.
%ifndef SHARED_INC
%define SHARED_INC

; int32_t mul_ints(int32_t a, int32_t b): a * b, wrapping like C#'s unchecked multiplication.
extern mul_ints

%endif
