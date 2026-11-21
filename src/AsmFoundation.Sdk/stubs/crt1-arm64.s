// =============================================================================
// crt1-arm64.s - the process entry of an executable for TargetFramework=libc
// on linux-arm64 (glibc) and linux-musl-arm64, the AArch64 twin of crt1.asm.
// musl's __libc_start_main takes main, argc and argv the same way, and
// ignores init, fini and the loader's fini, so one entry serves both.
//
// The dynamic loader has loaded and initialized glibc when it jumps here.
// _start hands control to __libc_start_main, which runs the program's
// initializers, calls main(argc, argv, envp) and passes main's result to
// exit(), flushing stdio: what glibc's own crt1.o does
// (sysdeps/aarch64/start.S), so the program is an ordinary
//     int main(int argc, char **argv)
// using any libc function. glibc 2.34 or later: init and fini are NULL, and
// glibc runs .init_array/.fini_array itself.
// =============================================================================

    .text
    .globl  _start
    .type   _start, %function
    .p2align 2
// _start - ELF e_entry. Never returns.
//   x0    the loader's termination function (rtld_fini), from the ABI
//   [sp]  argc, then argv[0..argc], NULL, envp..., NULL, auxv
_start:
    mov     x29, #0                 // outermost frame for debuggers and unwinders
    mov     x30, #0                 // no return address
    mov     x5, x0                  // rtld_fini (6th argument)
    ldr     x1, [sp]                // argc
    add     x2, sp, #8              // argv
    mov     x6, sp                  // stack_end (7th argument)
    // main's address through the GOT, as glibc's PIC start.S loads it: the
    // executable is position-independent.
    adrp    x0, :got:main
    ldr     x0, [x0, :got_lo12:main]
    mov     x3, #0                  // init = NULL (glibc 2.34+)
    mov     x4, #0                  // fini = NULL (glibc 2.34+)
    bl      __libc_start_main
    bl      abort                   // __libc_start_main never returns
    .size   _start, . - _start

    .section .note.GNU-stack,"",%progbits
