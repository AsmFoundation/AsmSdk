; calc for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"

section .text

; int32_t calc_add(int32_t a, int32_t b)
FUNCTION calc_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION

; int64_t calc_sum(const int32_t *values, size_t count)
; Each value is sign-extended before it is added, so the sum of many large values does not
; wrap at 32 bits. r10 is a scratch register on every OS (not an argument register here).
FUNCTION calc_sum, 2
    xor     eax, eax
    test    ARG2, ARG2
    jz      .done
.next:
    movsxd  r10, dword [ARG1]
    add     rax, r10
    add     ARG1, 4
    dec     ARG2
    jnz     .next
.done:
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
