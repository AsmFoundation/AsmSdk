// echo.s - EchoProgram for the ARM64 RIDs (win-arm64, linux-arm64, linux-musl-arm64,
// osx-arm64): see echo.asm. Prints each argument, then ASM_PROBE ("(unset)" without it), then
// the current directory; returns the number of arguments.
.include "asmsdk-gnu.inc"

RODATA
probe_name:  .asciz "ASM_PROBE"
unset_text:  .asciz "(unset)"
no_cwd_text: .asciz "(no cwd)"

.text

// int main(int argc, char **argv). The current directory goes into a 1 KiB buffer on the stack
// (below one page: no stack probe on Windows).
FUNCTION main
    stp     x29, x30, [sp, #-48]!   // the frame record, and room for x19-x21
    mov     x29, sp
    stp     x19, x20, [sp, #16]
    str     x21, [sp, #32]
    sub     sp, sp, #1024           // the directory buffer
    mov     w19, w0                 // argc
    mov     x20, x1                 // argv
    mov     w21, #1                 // argv[0] is the program itself
1:  cmp     w21, w19
    b.ge    2f
    ldr     x0, [x20, w21, uxtw #3]
    CALL    puts
    add     w21, w21, #1
    b       1b
2:  ADDRESS x0, probe_name
    CALL    getenv
    cbnz    x0, 3f
    ADDRESS x0, unset_text
3:  CALL    puts
    mov     x0, sp
    mov     x1, #1024
.ifdef WINDOWS
    CALL    _getcwd
.else
    CALL    getcwd
.endif
    cbnz    x0, 4f
    ADDRESS x0, no_cwd_text
4:  CALL    puts
    sub     w0, w19, #1             // the number of arguments
    add     sp, sp, #1024
    ldr     x21, [sp, #32]
    ldp     x19, x20, [sp, #16]
    ldp     x29, x30, [sp], #48
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
