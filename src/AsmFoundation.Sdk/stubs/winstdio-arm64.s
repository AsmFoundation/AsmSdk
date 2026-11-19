// =============================================================================
// winstdio-arm64.s - the AArch64 twin of winstdio-x64.asm: printf, fprintf,
// sprintf, snprintf, sscanf, vprintf, vfprintf, vsprintf, vsnprintf and atexit
// over the Universal C Runtime's common functions (TargetFramework=libc on
// win-arm64). Archived into asmsdk_winstdio.lib; never exported from a DLL.
//
// Variadic calls on Windows ARM64: the caller passes the first eight
// arguments in x0-x7 (floating-point ones too, in integer registers) and the
// rest on the stack. The callee stores x0-x7 into a 64-byte area right below
// the caller's stack arguments, which makes every argument one contiguous
// array, so a va_list is the address of the first variadic slot. Each
// variadic function below does that first (HOME), then saves x29/x30 below
// it: the home slot of register n is then at [sp, #16 + 8*n].
// Options and return values as in winstdio-x64.asm.
// =============================================================================

    .text

// HOME - x0-x7 into the area right below the caller's stack arguments, then
// the frame record; sp stays 16-byte aligned (64 + 16 bytes).
.macro HOME
    sub     sp, sp, #64
    stp     x0, x1, [sp, #0]
    stp     x2, x3, [sp, #16]
    stp     x4, x5, [sp, #32]
    stp     x6, x7, [sp, #48]
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
.endm

// UNHOME - the reverse of HOME, then return.
.macro UNHOME
    ldp     x29, x30, [sp], #16
    add     sp, sp, #64
    ret
.endm

// NEGATIVE_TO_MINUS_ONE - w0 < 0 becomes -1 (the sprintf family's contract):
// csinv keeps w0 when it is >= 0, else writes ~0.
.macro NEGATIVE_TO_MINUS_ONE
    cmp     w0, #0
    csinv   w0, w0, wzr, ge
.endm

// FUNC name - a global COFF function symbol, 4-byte aligned.
.macro FUNC name
    .globl  \name
    .def    \name
    .scl    2
    .type   32
    .endef
    .p2align 2
\name:
.endm

// int printf(const char *format, ...)
FUNC printf
    HOME
    mov     w0, #1
    bl      __acrt_iob_func         // stdout
    mov     x1, x0                  // stream
    mov     x0, #0                  // options
    ldr     x2, [sp, #16]           // format (home of x0)
    mov     x3, #0                  // locale
    add     x4, sp, #24             // the arguments after format (home of x1)
    bl      __stdio_common_vfprintf
    UNHOME

// int fprintf(FILE *stream, const char *format, ...)
FUNC fprintf
    HOME
    ldr     x1, [sp, #16]           // stream
    ldr     x2, [sp, #24]           // format
    mov     x0, #0
    mov     x3, #0
    add     x4, sp, #32             // the arguments after format
    bl      __stdio_common_vfprintf
    UNHOME

// int sprintf(char *buffer, const char *format, ...)
FUNC sprintf
    HOME
    ldr     x1, [sp, #16]           // buffer
    ldr     x3, [sp, #24]           // format
    mov     x0, #1                  // LEGACY_VSPRINTF_NULL_TERMINATION
    mov     x2, #-1                 // no size limit
    mov     x4, #0                  // locale
    add     x5, sp, #32             // the arguments after format
    bl      __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    UNHOME

// int snprintf(char *buffer, size_t count, const char *format, ...)
FUNC snprintf
    HOME
    ldr     x1, [sp, #16]           // buffer
    ldr     x2, [sp, #24]           // count
    ldr     x3, [sp, #32]           // format
    mov     x0, #2                  // STANDARD_SNPRINTF_BEHAVIOR
    mov     x4, #0
    add     x5, sp, #40             // the arguments after format
    bl      __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    UNHOME

// int sscanf(const char *buffer, const char *format, ...)
FUNC sscanf
    HOME
    ldr     x1, [sp, #16]           // buffer
    ldr     x3, [sp, #24]           // format
    mov     x0, #0
    mov     x2, #-1                 // up to the buffer's terminator
    mov     x4, #0
    add     x5, sp, #32             // the arguments after format
    bl      __stdio_common_vsscanf
    UNHOME

// int vprintf(const char *format, va_list arguments)
FUNC vprintf
    stp     x29, x30, [sp, #-32]!
    mov     x29, sp
    stp     x0, x1, [sp, #16]       // format and va_list survive the stdout call
    mov     w0, #1
    bl      __acrt_iob_func
    mov     x1, x0
    mov     x0, #0
    ldp     x2, x4, [sp, #16]       // format, va_list
    mov     x3, #0
    bl      __stdio_common_vfprintf
    ldp     x29, x30, [sp], #32
    ret

// int vfprintf(FILE *stream, const char *format, va_list arguments)
FUNC vfprintf
    mov     x4, x2                  // va_list
    mov     x2, x1                  // format
    mov     x1, x0                  // stream
    mov     x0, #0
    mov     x3, #0
    b       __stdio_common_vfprintf

// int vsprintf(char *buffer, const char *format, va_list arguments)
FUNC vsprintf
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     x5, x2                  // va_list
    mov     x3, x1                  // format
    mov     x1, x0                  // buffer
    mov     x0, #1
    mov     x2, #-1
    mov     x4, #0
    bl      __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    ldp     x29, x30, [sp], #16
    ret

// int vsnprintf(char *buffer, size_t count, const char *format, va_list arguments)
FUNC vsnprintf
    stp     x29, x30, [sp, #-16]!
    mov     x29, sp
    mov     x5, x3                  // va_list
    mov     x3, x2                  // format
    mov     x2, x1                  // count
    mov     x1, x0                  // buffer
    mov     x0, #2
    mov     x4, #0
    bl      __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    ldp     x29, x30, [sp], #16
    ret

// int atexit(void (*function)(void)) - the UCRT's _crt_atexit.
FUNC atexit
    b       _crt_atexit
