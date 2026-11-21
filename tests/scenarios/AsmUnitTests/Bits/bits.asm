; bits for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"

section .text

; int32_t bits_popcount(uint64_t x)
; POPCNT is part of x86-64-v2 (CPUs since 2008): every x64 machine the scenarios run on has it.
FUNCTION bits_popcount, 1
    popcnt  rax, ARG1
    RETURN
ENDFUNCTION

; int32_t bits_leading_zeros(uint64_t x)
; BSR rather than LZCNT, which older CPUs run as BSR with another result: BSR finds the
; highest set bit's index, and 63 - index (63 xor index, for 0..63) is the count. BSR sets ZF
; and leaves its destination undefined for 0, which is answered with 64 first.
FUNCTION bits_leading_zeros, 1
    mov     eax, 64
    bsr     r10, ARG1
    jz      .done
    mov     eax, 63
    xor     eax, r10d
.done:
    RETURN
ENDFUNCTION

; uint64_t bits_rotate_left(uint64_t x, int32_t n)
; ROL takes its count in cl and masks it to 6 bits, as BitOperations.RotateLeft does. x is
; copied out first: on Windows it arrives in rcx, the register cl belongs to.
FUNCTION bits_rotate_left, 2
    mov     rax, ARG1
    mov     ecx, ARG2d
    rol     rax, cl
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
