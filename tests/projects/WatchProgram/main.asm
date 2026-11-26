; main.asm - WatchProgram for the x64 RIDs: prints MESSAGE, which message.inc defines, and exits
; with 0. WatchTests edits message.inc while dotnet watch runs the program.
%include "message.inc"
extern puts

section .rodata
message: db MESSAGE, 0

section .text

; int main(void). The return address left rsp 8 bytes off a 16-byte boundary; the extra 8 align
; it for the call.
FUNCTION main
    sub     rsp, SHADOW_SPACE + 8
    lea     ARG1, [message]
    call    puts PLT
    xor     eax, eax
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
