// The ARM64 RIDs: the same program as hello.asm. Apple's ARM64 convention passes every
// variadic argument on the stack; Windows and Linux pass them in x1-x7 like the others.
.include "asmsdk-gnu.inc"

RODATA
fmt:    .asciz "hello from %s, %d + %d = %d\n"
.ifdef WINDOWS
os:     .asciz "win-arm64"
.endif
.ifdef MACOS
os:     .asciz "osx-arm64"
.endif
.ifdef LINUX
.ifdef MUSL
os:     .asciz "linux-musl-arm64"
.else
os:     .asciz "linux-arm64"
.endif
.endif
bye:    .asciz "bye from atexit"

LOCAL_FUNCTION on_exit_handler
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    ADDRESS x0, bye
    CALL    puts
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION on_exit_handler

FUNCTION main
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    sub     sp, sp, #32             // outgoing argument area, at the bottom of the frame
    ADDRESS x0, on_exit_handler
    CALL    atexit
    ADDRESS x0, fmt
    ADDRESS x1, os
.ifdef MACOS
    str     x1, [sp]
    mov     x2, #40
    str     x2, [sp, #8]
    mov     x2, #2
    str     x2, [sp, #16]
    mov     x2, #42
    str     x2, [sp, #24]
.else
    mov     w2, #40
    mov     w3, #2
    mov     w4, #42
.endif
    CALL    printf
    mov     w0, #3
    add     sp, sp, #32
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main
