// =============================================================================
// wincrt-arm64.s - the AArch64 twin of wincrt-x64.asm: the entry of a Windows
// executable for TargetFramework=libc on win-arm64, over the Universal C
// Runtime: console app type, the narrow argv and environment tables,
// main(argc, argv, envp), then exit(main's result). Linked with
// -entry:mainCRTStartup.
// =============================================================================

    .text
    .globl  mainCRTStartup
    .def    mainCRTStartup
    .scl    2
    .type   32
    .endef
    .p2align 2
// mainCRTStartup - the PE entry point. Never returns. x19 and x20 are
// callee-saved (AAPCS64, Windows included): argc and argv survive the calls.
mainCRTStartup:
    stp     x29, x30, [sp, #-32]!
    mov     x29, sp
    stp     x19, x20, [sp, #16]
    mov     w0, #1                  // _crt_console_app
    bl      _set_app_type
    mov     w0, #1                  // _crt_argv_unexpanded_arguments
    bl      _configure_narrow_argv
    bl      _initialize_narrow_environment
    bl      __p___argc              // int *
    ldr     w19, [x0]
    bl      __p___argv              // char ***
    ldr     x20, [x0]
    bl      __p__environ            // char ***
    ldr     x2, [x0]                // envp
    mov     w0, w19                 // argc
    mov     x1, x20                 // argv
    bl      main
    bl      exit                    // main's result (w0) is the exit code
    brk     #0xf000                 // exit never returns
