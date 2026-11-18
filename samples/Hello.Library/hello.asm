; =============================================================================
; hello.asm - libhello: a shared library built for both target frameworks
; (Hello.Library sample, TargetFrameworks=freestanding;libc).
;
; Exports three System V ABI functions that a C or .NET caller can use:
;     int hello_add(int a, int b);
;     int hello_version(void);
;     int hello_uses_libc(void);
; The code is position-independent (default rel), as a shared object requires.
; The SDK assembles this file once per target framework, with -DFREESTANDING
; or -DLIBC, the way a multi-targeted .csproj compiles with NET10_0 or
; NETSTANDARD2_0 defined.
; =============================================================================

default rel
bits 64

%include "hello.inc"

section .text

; -----------------------------------------------------------------------------
; hello_add - returns a + b (32-bit, wrapping).
; @param  edi  a
; @param  esi  b
; @returns eax = a + b
; -----------------------------------------------------------------------------
global hello_add:function
hello_add:
    lea     eax, [rdi + rsi]
    ret

; -----------------------------------------------------------------------------
; hello_version - returns HELLO_VERSION (major << 16 | minor).
; @returns eax = HELLO_VERSION
; -----------------------------------------------------------------------------
global hello_version:function
hello_version:
    mov     eax, HELLO_VERSION
    ret

; -----------------------------------------------------------------------------
; hello_uses_libc - tells the caller which build of the library it loaded.
; The answer is fixed at assembly time by the target framework's define, so a
; caller can check which build NuGet chose for it (a .NET program that
; references the project gets the libc build: the framework nearest to its
; own).
; @returns eax = 1 in the libc build, 0 in the freestanding build
; -----------------------------------------------------------------------------
global hello_uses_libc:function
hello_uses_libc:
%ifdef LIBC
    mov     eax, 1
%else
    xor     eax, eax                ; FREESTANDING
%endif
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
