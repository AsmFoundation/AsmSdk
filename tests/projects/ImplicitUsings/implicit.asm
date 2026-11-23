; implicit.asm - the x64 RIDs of ImplicitUsings: code only. asmsdk.inc, "default rel" and the
; Linux .note.GNU-stack section come from the SDK's pre-include (ImplicitUsings).

section .rodata
answer_value:   dd 42

section .text

; int32_t ib_add(int32_t a, int32_t b)
FUNCTION ib_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION

; int32_t ib_answer(void): 42 from read-only data. The operand names no base register, so it
; is RIP-relative only through the pre-included "default rel": an absolute address would not
; link into a Linux or macOS shared library, and NASM 3 warns about it under TreatWarningsAsErrors.
FUNCTION ib_answer, 0
    mov     eax, [answer_value]
    RETURN
ENDFUNCTION
