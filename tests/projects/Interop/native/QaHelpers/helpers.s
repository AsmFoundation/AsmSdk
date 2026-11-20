// helpers.s - QaHelpers, ARM64: int64_t qa_helper_square(int64_t x), hidden.
.include "asmsdk-gnu.inc"

HIDDEN_FUNCTION qa_helper_square
    mul     x0, x0, x0
    ret
ENDFUNCTION qa_helper_square

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
