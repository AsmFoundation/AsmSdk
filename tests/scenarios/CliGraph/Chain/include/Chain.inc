; Chain.inc - the public interface of the Chain static library, for NASM sources.
;
; A project that references Chain has this folder on its include path:
;   %include "Chain.inc"
; GNU-syntax (ARM64) sources need no declaration: llvm-mc treats a name it does not define as
; external, and the linker finds it in the library.
%ifndef CHAIN_INC
%define CHAIN_INC

; int32_t square_ints(int32_t a): a * a, through Shared's mul_ints.
extern square_ints

%endif
