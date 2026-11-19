; =============================================================================
; nonshared-x64.asm - the C library functions that glibc does not export from
; libc.so.6 but links statically from libc_nonshared.a (TargetFramework=libc on
; linux-x64 and linux-musl-x64). The SDK archives this object into
; libasmsdk_nonshared.a and puts it on every Linux libc link line, so a
; function is linked only into a program that calls it.
;
; atexit is the one that matters: measured, glibc 2.39's libc.so.6 exports
; every name of linux.symbols but atexit, so a program calling it linked but
; failed to start ("undefined symbol: atexit"). musl exports atexit itself;
; this definition works there too, through __cxa_atexit.
;
; hidden: the function never becomes an export of a shared library that links
; it, just as libc_nonshared.a's would not.
; =============================================================================

bits 64
default rel

extern __cxa_atexit

section .text

; int atexit(void (*function)(void))
; Registers function to run at exit: __cxa_atexit(function, NULL, NULL), what
; glibc's libc_nonshared.a does. The NULL DSO handle ties the handler to the
; whole process (it runs at exit, never at a dlclose), which is right for a
; program and acceptable for a library.
global atexit:function hidden
atexit:
    xor     esi, esi                    ; the handler's argument: none
    xor     edx, edx                    ; DSO handle: none
    jmp     __cxa_atexit wrt ..plt

section .note.GNU-stack noalloc noexec nowrite progbits
