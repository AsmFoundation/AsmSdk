// The ARM64 RIDs of Everywhere: win-arm64, linux-arm64, osx-arm64.
.include "asmsdk-gnu.inc"

RODATA
    .p2align 2
base:   .word 100

FUNCTION ew_add
    add     w0, w0, w1
    ret
ENDFUNCTION ew_add

FUNCTION ew_platform
.ifdef WINDOWS
    mov     w0, #2
.endif
.ifdef LINUX
    mov     w0, #4
.endif
.ifdef MACOS
    mov     w0, #7
.endif
    ret
ENDFUNCTION ew_platform

FUNCTION ew_os
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    CALL    ew_hidden
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION ew_os

// Not global: no OS exports it.
LOCAL_FUNCTION ew_hidden
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    CALL    osname
    ADDRESS x1, base
    ldr     w1, [x1]
    add     w0, w0, w1
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION ew_hidden
