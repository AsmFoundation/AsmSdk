// =============================================================================
// nonshared-arm64.s - the AArch64 twin of nonshared-x64.asm: the C library
// functions glibc links statically from libc_nonshared.a instead of exporting
// them (TargetFramework=libc on linux-arm64). Archived into
// libasmsdk_nonshared.a, so a function is linked only when called.
// =============================================================================

    .text
    // int atexit(void (*function)(void)): __cxa_atexit(function, NULL, NULL),
    // as glibc's libc_nonshared.a does; hidden, so never a library's export.
    .globl  atexit
    .hidden atexit
    .type   atexit, %function
    .p2align 2
atexit:
    mov     x1, xzr                 // the handler's argument: none
    mov     x2, xzr                 // DSO handle: none
    b       __cxa_atexit
    .size   atexit, . - atexit

    .section .note.GNU-stack,"",%progbits
