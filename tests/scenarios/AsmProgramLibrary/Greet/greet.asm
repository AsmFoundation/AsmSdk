; greet for the x64 RIDs: win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"
extern puts
extern calc_mul

section .rodata
message:    db "greet: hello from an asm program", 0

section .text

; int main(void): prints the message, then returns calc_mul(6, 7) from the shared library.
FUNCTION main
    ; rsp is 16n+8 at entry; SHADOW_SPACE + 8 (40 on Windows, 8 elsewhere) aligns it to 16 for
    ; the calls and reserves the 32 bytes of shadow space Windows callees may use.
    sub     rsp, SHADOW_SPACE + 8
    lea     ARG1, [message]
    call    puts PLT
    mov     ARG1d, 6
    mov     ARG2d, 7
    call    calc_mul PLT            ; resolved at run time, from the library next to the program
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
