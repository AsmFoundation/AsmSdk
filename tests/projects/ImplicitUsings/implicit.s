// implicit.s - the ARM64 RIDs of ImplicitUsings: code only. asmsdk-gnu.inc and the Linux
// .note.GNU-stack section come from the wrapper the SDK generates around this file
// (ImplicitUsings).

RODATA
answer_value:
    .word   42

// int32_t ib_add(int32_t a, int32_t b)
FUNCTION ib_add
    add     w0, w0, w1
    ret
ENDFUNCTION ib_add

// int32_t ib_answer(void): 42 from read-only data, through ADDRESS (adrp + add, or the
// Mach-O and COFF equivalents), a macro of the pre-included asmsdk-gnu.inc.
FUNCTION ib_answer
    ADDRESS x1, answer_value
    ldr     w0, [x1]
    ret
ENDFUNCTION ib_answer
