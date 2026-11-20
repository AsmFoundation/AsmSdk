; =============================================================================
; qalibc.asm - QaLibc (TargetFramework=libc): functions that use the platform's
; C library, called from C#. Memory comes from malloc and goes back through
; qa_free (the caller never frees it itself); snprintf goes through each OS's
; C library (on Windows the SDK's wrapper over the UCRT); qsort calls a
; comparator written here. x64 RIDs only: on ARM64 the library does not exist,
; which the C# side handles (an optional native component).
; =============================================================================

default rel
%include "asmsdk.inc"
extern strlen, malloc, free, snprintf, qsort

section .rodata
format:     db "value=%lld", 0

section .text

; char *qa_dup_reverse(const char *s): a malloc'ed, reversed copy; NULL when
; malloc fails. Release it with qa_free.
FUNCTION qa_dup_reverse, 1, rbx, r12, r13
    sub     rsp, SHADOW_SPACE           ; 3 pushes: already 16-aligned
    mov     rbx, ARG1
    call    strlen PLT
    mov     r12, rax                    ; length
    lea     ARG1, [rax + 1]
    call    malloc PLT
    test    rax, rax
    jz      .done
    mov     r13, rax
    xor     r8d, r8d                    ; i
.copy:
    cmp     r8, r12
    jae     .terminate
    mov     r9, r12
    sub     r9, r8
    movzx   r10d, byte [rbx + r9 - 1]   ; s[length - 1 - i]
    mov     [r13 + r8], r10b
    inc     r8
    jmp     .copy
.terminate:
    mov     byte [r13 + r12], 0
    mov     rax, r13
.done:
    add     rsp, SHADOW_SPACE
    RETURN
ENDFUNCTION

; void qa_free(void *p): a tail call, free returns straight to our caller.
FUNCTION qa_free, 1
    jmp     free PLT
ENDFUNCTION

; int qa_format(char *buffer, size_t capacity, int64_t value): snprintf with
; "value=%lld"; returns what snprintf returns.
FUNCTION qa_format, 3
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG4, ARG3                  ; value: the 4th argument of snprintf
    lea     ARG3, [format]
%ifndef WINDOWS
    xor     eax, eax                    ; System V varargs: no vector registers
%endif
    call    snprintf PLT
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; void qa_sort_i32(int32_t *values, size_t count): qsort with compare_i32.
FUNCTION qa_sort_i32, 2
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG3, 4                     ; the element size
    lea     ARG4, [compare_i32]
    call    qsort PLT
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; int compare_i32(const void *a, const void *b): -1, 0 or 1, called by qsort.
LOCAL_FUNCTION compare_i32, 2
    mov     r8d, [ARG1]
    mov     r9d, [ARG2]
    xor     eax, eax
    cmp     r8d, r9d
    setg    al
    setl    r10b
    movzx   r10d, r10b
    sub     eax, r10d
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
