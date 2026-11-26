; echo.asm - the asm-echo tool for the x64 RIDs (win-x64, linux-x64, linux-musl-x64, osx-x64):
; what a command gets from its caller. Prints each argument on a line of its own, then the value
; of ASM_PROBE ("(unset)" without it), then the current directory, and returns the number of
; arguments, so a scenario sees from the output and the exit status that dotnet tool run, a local
; tool's dotnet command and dotnet tool exec hand the arguments over unchanged.
extern puts
extern getenv
%ifdef WINDOWS
extern _getcwd
%define GETCWD _getcwd
%else
extern getcwd
%define GETCWD getcwd
%endif

section .rodata
probe_name:  db "ASM_PROBE", 0
unset_text:  db "(unset)", 0
no_cwd_text: db "(no cwd)", 0

section .text

; int main(int argc, char **argv). The current directory goes into a 1 KiB buffer on the stack
; (below one page: no stack probe on Windows), above the callee's shadow space.
FUNCTION main
    push    rbx
    push    rbp
    push    r12
    sub     rsp, SHADOW_SPACE + 1024    ; three pushes left rsp 16-aligned; both sizes keep it so
    mov     ebx, ARG1d                  ; argc
    mov     rbp, ARG2                   ; argv
    mov     r12d, 1                     ; argv[0] is the program itself: start at the first argument
.next_argument:
    cmp     r12d, ebx
    jge     .environment
    mov     ARG1, [rbp + r12*8]
    call    puts PLT
    inc     r12d
    jmp     .next_argument
.environment:
    lea     ARG1, [probe_name]
    call    getenv PLT
    test    rax, rax
    jnz     .print_environment
    lea     rax, [unset_text]
.print_environment:
    mov     ARG1, rax
    call    puts PLT
    lea     ARG1, [rsp + SHADOW_SPACE]
    mov     ARG2, 1024
    call    GETCWD PLT
    test    rax, rax
    jnz     .print_directory
    lea     rax, [no_cwd_text]
.print_directory:
    mov     ARG1, rax
    call    puts PLT
    lea     eax, [rbx - 1]              ; the number of arguments
    add     rsp, SHADOW_SPACE + 1024
    pop     r12
    pop     rbp
    pop     rbx
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
