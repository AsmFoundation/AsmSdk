; =============================================================================
; winstdio-x64.asm - the C functions a Windows libc program expects but the
; Universal C Runtime does not export (TargetFramework=libc on win-x64): the
; printf and scanf families, which the UCRT headers define as inline functions
; over __stdio_common_vfprintf / __stdio_common_vsprintf /
; __stdio_common_vsscanf, and atexit, which is _crt_atexit there. The SDK
; archives this object into asmsdk_winstdio.lib and links it into every
; Windows libc executable and DLL; the functions are not exported from a DLL.
;
; Variadic calls in the Microsoft x64 convention: the caller passes the first
; four arguments in rcx, rdx, r8, r9 and the rest on the stack, above the
; 32-byte home area it reserved. Storing rcx..r9 into that home area makes
; every argument one contiguous array, so a va_list is just the address of
; the first variadic slot. Each function below homes its register arguments
; first, then calls the UCRT's common function with that address.
;
; Options, as the UCRT headers pass them by default: 0 for printf, fprintf
; and sscanf; for sprintf LEGACY_VSPRINTF_NULL_TERMINATION (1, terminate even
; when the result is cut); for snprintf STANDARD_SNPRINTF_BEHAVIOR (2, return
; the full length C99-style). The sprintf family returns -1 for any negative
; result, as the headers' wrappers do.
;
; Stack: rsp is 16n+8 at entry; sub rsp, 56 leaves it 16-byte aligned, with
; the callee's 32-byte shadow space at [rsp], the 5th and 6th arguments at
; [rsp+32] and [rsp+40], the return address at [rsp+56] and the home slots of
; rcx, rdx, r8, r9 at [rsp+64], [rsp+72], [rsp+80], [rsp+88].
; =============================================================================

bits 64
default rel

%define CRT_LEGACY_VSPRINTF_NULL_TERMINATION 1
%define CRT_STANDARD_SNPRINTF_BEHAVIOR 2

extern __acrt_iob_func, __stdio_common_vfprintf, __stdio_common_vsprintf
extern __stdio_common_vsscanf, _crt_atexit

section .text

; HOME_REGISTERS - stores rcx, rdx, r8, r9 into the caller's home area.
%macro HOME_REGISTERS 0
    mov     [rsp+8], rcx
    mov     [rsp+16], rdx
    mov     [rsp+24], r8
    mov     [rsp+32], r9
%endmacro

; NEGATIVE_TO_MINUS_ONE - eax < 0 becomes -1 (the sprintf family's contract).
%macro NEGATIVE_TO_MINUS_ONE 0
    test    eax, eax
    jns     %%done
    mov     eax, -1
%%done:
%endmacro

; int printf(const char *format, ...)
global printf
printf:
    HOME_REGISTERS
    sub     rsp, 56
    mov     ecx, 1
    call    __acrt_iob_func             ; stdout
    xor     ecx, ecx                    ; options
    mov     rdx, rax                    ; stream
    mov     r8, [rsp+64]                ; format
    xor     r9d, r9d                    ; locale
    lea     rax, [rsp+72]               ; the arguments after format
    mov     [rsp+32], rax
    call    __stdio_common_vfprintf
    add     rsp, 56
    ret

; int fprintf(FILE *stream, const char *format, ...)
global fprintf
fprintf:
    HOME_REGISTERS
    sub     rsp, 56
    mov     rdx, rcx                    ; stream
    xor     ecx, ecx                    ; options
    mov     r8, [rsp+72]                ; format
    xor     r9d, r9d                    ; locale
    lea     rax, [rsp+80]               ; the arguments after format
    mov     [rsp+32], rax
    call    __stdio_common_vfprintf
    add     rsp, 56
    ret

; int sprintf(char *buffer, const char *format, ...)
global sprintf
sprintf:
    HOME_REGISTERS
    sub     rsp, 56
    mov     rdx, rcx                    ; buffer
    mov     ecx, CRT_LEGACY_VSPRINTF_NULL_TERMINATION
    mov     r8, -1                      ; no size limit
    mov     r9, [rsp+72]                ; format
    mov     qword [rsp+32], 0           ; locale
    lea     rax, [rsp+80]               ; the arguments after format
    mov     [rsp+40], rax
    call    __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    add     rsp, 56
    ret

; int snprintf(char *buffer, size_t count, const char *format, ...)
global snprintf
snprintf:
    HOME_REGISTERS
    sub     rsp, 56
    mov     r9, r8                      ; format
    mov     r8, rdx                     ; count
    mov     rdx, rcx                    ; buffer
    mov     ecx, CRT_STANDARD_SNPRINTF_BEHAVIOR
    mov     qword [rsp+32], 0           ; locale
    lea     rax, [rsp+88]               ; the arguments after format
    mov     [rsp+40], rax
    call    __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    add     rsp, 56
    ret

; int sscanf(const char *buffer, const char *format, ...)
global sscanf
sscanf:
    HOME_REGISTERS
    sub     rsp, 56
    mov     rdx, rcx                    ; buffer
    xor     ecx, ecx                    ; options
    mov     r8, -1                      ; buffer length: up to its terminator
    mov     r9, [rsp+72]                ; format
    mov     qword [rsp+32], 0           ; locale
    lea     rax, [rsp+80]               ; the arguments after format
    mov     [rsp+40], rax
    call    __stdio_common_vsscanf
    add     rsp, 56
    ret

; int vprintf(const char *format, va_list arguments)
global vprintf
vprintf:
    HOME_REGISTERS                      ; format and va_list survive the stdout call
    sub     rsp, 56
    mov     ecx, 1
    call    __acrt_iob_func             ; stdout
    xor     ecx, ecx
    mov     rdx, rax
    mov     r8, [rsp+64]                ; format
    xor     r9d, r9d
    mov     rax, [rsp+72]               ; va_list
    mov     [rsp+32], rax
    call    __stdio_common_vfprintf
    add     rsp, 56
    ret

; int vfprintf(FILE *stream, const char *format, va_list arguments)
global vfprintf
vfprintf:
    sub     rsp, 56
    mov     [rsp+32], r8                ; va_list
    mov     r8, rdx                     ; format
    mov     rdx, rcx                    ; stream
    xor     ecx, ecx
    xor     r9d, r9d
    call    __stdio_common_vfprintf
    add     rsp, 56
    ret

; int vsprintf(char *buffer, const char *format, va_list arguments)
global vsprintf
vsprintf:
    sub     rsp, 56
    mov     [rsp+40], r8                ; va_list
    mov     qword [rsp+32], 0           ; locale
    mov     r9, rdx                     ; format
    mov     rdx, rcx                    ; buffer
    mov     r8, -1
    mov     ecx, CRT_LEGACY_VSPRINTF_NULL_TERMINATION
    call    __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    add     rsp, 56
    ret

; int vsnprintf(char *buffer, size_t count, const char *format, va_list arguments)
global vsnprintf
vsnprintf:
    sub     rsp, 56
    mov     [rsp+40], r9                ; va_list
    mov     qword [rsp+32], 0           ; locale
    mov     r9, r8                      ; format
    mov     r8, rdx                     ; count
    mov     rdx, rcx                    ; buffer
    mov     ecx, CRT_STANDARD_SNPRINTF_BEHAVIOR
    call    __stdio_common_vsprintf
    NEGATIVE_TO_MINUS_ONE
    add     rsp, 56
    ret

; int atexit(void (*function)(void)) - the UCRT's _crt_atexit, same arguments.
global atexit
atexit:
    jmp     _crt_atexit
