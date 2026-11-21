; =============================================================================
; wincrt-x64.asm - the entry of a Windows executable for TargetFramework=libc
; (win-x64): what the MSVC runtime's mainCRTStartup does for a C program,
; reduced to what an assembly program needs, over the Universal C Runtime
; (ucrtbase.dll, part of Windows 10 and later):
;   1. _set_app_type(console), so the UCRT reports errors on the console;
;   2. the narrow argv and environment tables (the UCRT parses the command
;      line; arguments are not wildcard-expanded, as in a default C program);
;   3. main(argc, argv, envp);
;   4. exit(main's result), which runs atexit handlers and flushes stdio.
; The program defines an ordinary int main(int argc, char **argv).
; The SDK links it with -entry:mainCRTStartup.
; =============================================================================

bits 64
default rel

extern main
extern _set_app_type, _configure_narrow_argv, _initialize_narrow_environment
extern __p___argc, __p___argv, __p__environ, exit

section .text

; mainCRTStartup - the PE entry point. Never returns.
; rsp is 16n+8 at entry, as after any call; three pushes and 32 bytes of
; shadow space leave it 16-byte aligned for the calls below. rbx, rsi and rdi
; are callee-saved in the Microsoft x64 convention, so argc and argv survive
; the calls in them.
global mainCRTStartup
mainCRTStartup:
    push    rbx
    push    rsi
    push    rdi
    sub     rsp, 32                     ; shadow space for the callees
    mov     ecx, 1                      ; _crt_console_app
    call    _set_app_type
    mov     ecx, 1                      ; _crt_argv_unexpanded_arguments
    call    _configure_narrow_argv
    call    _initialize_narrow_environment
    call    __p___argc                  ; int *
    mov     ebx, [rax]
    call    __p___argv                  ; char ***
    mov     rsi, [rax]
    call    __p__environ                ; char ***
    mov     r8, [rax]                   ; envp
    mov     ecx, ebx                    ; argc
    mov     rdx, rsi                    ; argv
    call    main
    mov     ecx, eax                    ; main's result is the exit code
    call    exit
    int3                                ; exit never returns
