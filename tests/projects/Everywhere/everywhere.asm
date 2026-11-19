; The x64 RIDs of Everywhere: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"
extern osname

section .rodata
; A constant in read-only data, read RIP-relative (on Windows from .rdata: the SDK maps the
; Linux section name, which NASM's win64 output would otherwise make executable code).
base:   dd 100

section .text

FUNCTION ew_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION

FUNCTION ew_platform
%ifdef WINDOWS
    mov     eax, 1
%elifdef MACOS
    mov     eax, 6
%elifdef MUSL
    mov     eax, 5
%else
    mov     eax, 3
%endif
    RETURN
ENDFUNCTION

FUNCTION ew_os
    sub     rsp, SHADOW_SPACE + 8
    call    ew_hidden
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; Not global: no OS exports it.
LOCAL_FUNCTION ew_hidden
    sub     rsp, SHADOW_SPACE + 8
    call    osname PLT
    add     rsp, SHADOW_SPACE + 8
    lea     rcx, [base]
    add     eax, [rcx]
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
