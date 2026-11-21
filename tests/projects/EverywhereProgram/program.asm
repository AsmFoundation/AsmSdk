; The x64 RIDs of EverywhereProgram (and of EverywherePackageProgram and ProgramLayoutWindows,
; which assemble this file too): win-x64, linux-x64, linux-musl-x64, osx-x64.
default rel
%include "asmsdk.inc"
extern puts
extern ew_platform

section .text

; int main(int argc, char **argv): prints argv[argc - 1], then returns
; 10 * ew_platform() + argc - 1. puts, not printf: Apple's ARM64 ABI passes variadic
; arguments on the stack, so one source of the call would not serve every OS.
FUNCTION main
    push    rbx
    push    rbp
    sub     rsp, SHADOW_SPACE + 8   ; after two pushes, 40 bytes keep rsp 16-aligned for the calls
    mov     ebx, ARG1d              ; argc
    mov     rbp, ARG2               ; argv
    movsxd  rax, ebx
    mov     ARG1, [rbp + rax*8 - 8] ; argv[argc - 1]
    call    puts PLT
    call    ew_platform PLT         ; from the shared library, found at run time
    imul    eax, eax, 10
    lea     eax, [rax + rbx - 1]    ; + the number of arguments
    add     rsp, SHADOW_SPACE + 8
    pop     rbp
    pop     rbx
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
