// linux-arm64 of SystemLd: int sl_answer(void) = 42. Never assembled by the tests (the build
// stops at ASM0005 first), present so that every listed RID has a source.
.include "asmsdk-gnu.inc"

FUNCTION sl_answer
    mov     w0, #42
    ret
ENDFUNCTION sl_answer
