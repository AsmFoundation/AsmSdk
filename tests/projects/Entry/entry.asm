; =============================================================================
; entry.asm - the Entry test project: a freestanding executable that starts at
; entry_here (StartupObject) instead of _start.
; =============================================================================

default rel
bits 64

%define SYS_EXIT_GROUP  231

section .text

; -----------------------------------------------------------------------------
; entry_here - process entry (static executable: the kernel jumps here).
; Never returns; exits with status 42, which RunTests (tests/AsmSdk.Tests) expects.
; -----------------------------------------------------------------------------
global entry_here:function
entry_here:
    mov     edi, 42
    mov     eax, SYS_EXIT_GROUP
    syscall

section .note.GNU-stack noalloc noexec nowrite progbits
