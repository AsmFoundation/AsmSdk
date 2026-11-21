// Generic ARM64: every ARM64 RID without a closer osname.* file (linux-arm64, osx-arm64).
.include "asmsdk-gnu.inc"
HIDDEN_FUNCTION osname
    mov     w0, #20
    ret
ENDFUNCTION osname
