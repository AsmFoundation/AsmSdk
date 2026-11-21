; The x64 RIDs: printf with five arguments (the fifth on the stack on Windows), an atexit
; handler, exit code 3.
default rel
%include "asmsdk.inc"
extern printf, puts, atexit

section .rodata
fmt:    db "hello from %s, %d + %d = %d", 10, 0
%ifdef WINDOWS
os:     db "win-x64", 0
%elifdef MACOS
os:     db "osx-x64", 0
%elifdef MUSL
os:     db "linux-musl-x64", 0
%else
os:     db "linux-x64", 0
%endif
bye:    db "bye from atexit", 0

section .text
LOCAL_FUNCTION on_exit_handler
    sub     rsp, SHADOW_SPACE + 8
    lea     ARG1, [bye]
    call    puts PLT
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

FUNCTION main
    sub     rsp, SHADOW_SPACE + 24          ; Windows: shadow space + argument 5; 16-aligned
    lea     ARG1, [on_exit_handler]
    call    atexit PLT
    lea     ARG1, [fmt]
    lea     ARG2, [os]
    mov     ARG3d, 40
    mov     ARG4d, 2
%ifdef WINDOWS
    mov     qword [rsp + 32], 42            ; argument 5 goes on the stack on Windows
%else
    mov     ARG5d, 42
    xor     eax, eax                        ; System V varargs: no vector registers used
%endif
    call    printf PLT
    mov     eax, 3
    add     rsp, SHADOW_SPACE + 24
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
