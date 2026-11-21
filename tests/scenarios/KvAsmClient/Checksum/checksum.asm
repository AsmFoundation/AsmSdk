; checksum for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"

section .text

; uint64_t checksum_fnv1a(const uint8_t *data, size_t length)
; FNV-1a, 64-bit: start from the offset basis, then for each byte xor it in and multiply by
; the FNV prime (modulo 2^64). r10 and r11 are scratch registers on every OS, and with two
; arguments the Windows entry code does not load them from the stack.
FUNCTION checksum_fnv1a, 2
    mov     rax, 0xcbf29ce484222325     ; offset basis
    mov     r10, 0x100000001b3          ; prime
    test    ARG2, ARG2
    jz      .done
.next:
    movzx   r11d, byte [ARG1]
    xor     rax, r11
    imul    rax, r10
    inc     ARG1
    dec     ARG2
    jnz     .next
.done:
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
