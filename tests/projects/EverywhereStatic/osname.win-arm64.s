// Exactly win-arm64 (a RID qualifier, the closest there is).
.include "asmsdk-gnu.inc"
FUNCTION osname
    mov     w0, #21
    ret
ENDFUNCTION osname
