; =============================================================================
; crt1.asm - the process entry of an AsmRuntime=Libc executable.
;
; The dynamic loader has already loaded and initialized glibc when it jumps
; here. _start hands control to glibc's __libc_start_main, which runs the
; program's initializers, calls main(argc, argv, envp) and passes main's return
; value to exit(), flushing stdio. This is exactly what glibc's own crt1.o does
; (sysdeps/x86_64/start.S), so an assembly program can define an ordinary
;     int main(int argc, char **argv)
; and use any libc function.
;
; The program counts on glibc 2.34 or later: init and fini are passed as NULL,
; and glibc then runs the executable's .init_array/.fini_array itself.
; =============================================================================

default rel
bits 64

extern main
extern __libc_start_main

section .text

; -----------------------------------------------------------------------------
; _start - entry point (ELF e_entry). Never returns.
; @param  rdx  the loader's termination function (rtld_fini), from the ABI
; @param  [rsp] argc, then argv[0..argc], NULL, envp..., NULL, auxv
; @remarks rbp is zeroed to mark the outermost frame for debuggers and
;          unwinders. The stack is realigned to 16 bytes before the call, as
;          the System V ABI requires; the push of rax keeps that alignment once
;          stack_end is pushed as the seventh (stack) argument.
; -----------------------------------------------------------------------------
global _start:function
_start:
    xor     ebp, ebp                    ; outermost frame
    mov     r9, rdx                     ; rtld_fini
    pop     rsi                         ; argc
    mov     rdx, rsp                    ; argv
    and     rsp, -16
    push    rax                         ; padding: 16-byte alignment after the next push
    push    rsp                         ; stack_end (7th argument, on the stack)
    xor     r8d, r8d                    ; fini = NULL (glibc 2.34+)
    xor     ecx, ecx                    ; init = NULL (glibc 2.34+)
    lea     rdi, [rel main]             ; main is defined in this executable
    call    [rel __libc_start_main wrt ..got]   ; through the GOT (R_X86_64_GOTPCREL)
    hlt                                 ; __libc_start_main never returns

section .note.GNU-stack noalloc noexec nowrite progbits
