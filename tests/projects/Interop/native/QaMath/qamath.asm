; =============================================================================
; qamath.asm - the x64 half of QaMath (win-x64, linux-x64, linux-musl-x64,
; osx-x64): one function per C ABI case a C# caller can hit, written once for
; Windows' and System V's conventions through asmsdk.inc. qamath.s is the ARM64
; half, with the same exports. The C prototypes are in include/qamath.h.
;
; Where the conventions differ beyond what ARG1..ARG6 name, the function says
; so with %ifdef WINDOWS: floating-point arguments (positional on Windows,
; sequential on System V), arguments 7 and 8 (on the stack at different
; offsets), and a 16-byte struct return (a hidden pointer on Windows).
; =============================================================================

default rel
%include "asmsdk.inc"

extern qa_helper_square                 ; QaHelpers (a static library): HIDDEN_FUNCTION

section .rodata
; qa_version's answer: the RID this build is for, so a caller can prove the
; right file was loaded.
%ifdef WINDOWS
version:    db "qamath 1.0 win-x64", 0
%elifdef MACOS
version:    db "qamath 1.0 osx-x64", 0
%elifdef MUSL
version:    db "qamath 1.0 linux-musl-x64", 0
%else
version:    db "qamath 1.0 linux-x64", 0
%endif

section .data
align 8
counter:    dq 0                        ; qa_counter_add's shared value

section .text

; int32_t qa_add(int32_t a, int32_t b)
FUNCTION qa_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION

; int64_t qa_sum_i64(const int64_t *values, size_t count)
FUNCTION qa_sum_i64, 2
    xor     eax, eax
    test    ARG2, ARG2
    jz      .done
.loop:
    add     rax, [ARG1]
    add     ARG1, 8
    dec     ARG2
    jnz     .loop
.done:
    RETURN
ENDFUNCTION

; double qa_mix(int64_t a, double b, int64_t c, double d): a*b + c*d.
; Windows numbers the arguments by position whatever their type (a rcx,
; b xmm1, c r8, d xmm3); System V counts integers and floats apart (a rdi,
; b xmm0, c rsi, d xmm1).
FUNCTION qa_mix, 4
%ifdef WINDOWS
    cvtsi2sd xmm0, rcx
    mulsd   xmm0, xmm1
    cvtsi2sd xmm2, r8
    mulsd   xmm2, xmm3
    addsd   xmm0, xmm2
%else
    cvtsi2sd xmm2, rdi
    mulsd   xmm2, xmm0
    cvtsi2sd xmm3, rsi
    mulsd   xmm3, xmm1
    addsd   xmm2, xmm3
    movapd  xmm0, xmm2
%endif
    RETURN
ENDFUNCTION

; int64_t qa_many(int64_t a1, ..., int64_t a8): 1*a1 + 2*a2 + ... + 8*a8.
; FUNCTION loads arguments 5 and 6 on Windows; 7 and 8 are on the stack on
; both: above the shadow space and arguments 5-6 on Windows, right above the
; return address on System V (nothing is pushed here, so the offsets hold).
%ifdef WINDOWS
  %define QA_ARG7 [rsp + 56]
  %define QA_ARG8 [rsp + 64]
%else
  %define QA_ARG7 [rsp + 8]
  %define QA_ARG8 [rsp + 16]
%endif
FUNCTION qa_many, 6
    mov     rax, ARG1
    imul    ARG2, ARG2, 2
    add     rax, ARG2
    imul    ARG3, ARG3, 3
    add     rax, ARG3
    imul    ARG4, ARG4, 4
    add     rax, ARG4
    imul    ARG5, ARG5, 5
    add     rax, ARG5
    imul    ARG6, ARG6, 6
    add     rax, ARG6
    mov     ARG1, QA_ARG7
    imul    ARG1, ARG1, 7
    add     rax, ARG1
    mov     ARG1, QA_ARG8
    imul    ARG1, ARG1, 8
    add     rax, ARG1
    RETURN
ENDFUNCTION

; int64_t qa_dot3(const vec3i *a, const vec3i *b), vec3i = { int32_t x, y, z }
FUNCTION qa_dot3, 2
    movsxd  rax, dword [ARG1]
    movsxd  r8, dword [ARG2]
    imul    rax, r8
    movsxd  r9, dword [ARG1 + 4]
    movsxd  r8, dword [ARG2 + 4]
    imul    r9, r8
    add     rax, r9
    movsxd  r9, dword [ARG1 + 8]
    movsxd  r8, dword [ARG2 + 8]
    imul    r9, r8
    add     rax, r9
    RETURN
ENDFUNCTION

; int64_t qa_pair_sum(pair p), pair = { int32_t a, b } passed BY VALUE: an
; 8-byte struct travels in one integer register on both conventions (a in the
; low half, b in the high half).
FUNCTION qa_pair_sum, 1
    mov     rax, ARG1
    sar     rax, 32
    movsxd  ARG1, ARG1d
    add     rax, ARG1
    RETURN
ENDFUNCTION

; pair qa_make_pair(int32_t a, int32_t b): an 8-byte struct comes back in rax
; on both conventions.
FUNCTION qa_make_pair, 2
    mov     eax, ARG1d
    shl     ARG2, 32
    or      rax, ARG2
    RETURN
ENDFUNCTION

; quad qa_make_quad(int64_t x, int64_t y), quad = { int64_t x, y }: a 16-byte
; struct. System V returns it in rax:rdx. Windows returns any struct that is
; not 1, 2, 4 or 8 bytes through a hidden pointer: the caller's buffer is
; the first argument (rcx), the real arguments move up one (rdx, r8), and
; the buffer's address comes back in rax. asmsdk.inc cannot hide that shift.
FUNCTION qa_make_quad, 3
%ifdef WINDOWS
    mov     [rcx], rdx
    mov     [rcx + 8], r8
    mov     rax, rcx
%else
    mov     rax, rdi
    mov     rdx, rsi
%endif
    RETURN
ENDFUNCTION

; int64_t qa_reduce(const int32_t *values, size_t count,
;                   int64_t (*op)(int64_t acc, int32_t value))
; Calls op (a C# function) for every value, threading the accumulator.
; rbx, r12-r14 hold the state across the calls (callee-saved on both).
FUNCTION qa_reduce, 3, rbx, r12, r13, r14
    sub     rsp, SHADOW_SPACE + 8       ; 4 pushes + 8 = 16-aligned at the call
    mov     rbx, ARG1
    mov     r12, ARG2
    mov     r13, ARG3
    xor     r14d, r14d
    test    r12, r12
    jz      .done
.loop:
    mov     ARG1, r14
    mov     ARG2d, [rbx]
    call    r13
    mov     r14, rax
    add     rbx, 4
    dec     r12
    jnz     .loop
.done:
    mov     rax, r14
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; const char *qa_version(void): a static string; the caller must not free it.
FUNCTION qa_version
    lea     rax, [version]
    RETURN
ENDFUNCTION

; int64_t qa_utf8_count(const char *s): code points of a UTF-8 string (the
; bytes that are not 10xxxxxx continuation bytes).
FUNCTION qa_utf8_count, 1
    xor     eax, eax
.loop:
    movzx   r8d, byte [ARG1]
    test    r8d, r8d
    jz      .done
    inc     ARG1
    and     r8d, 0xC0
    cmp     r8d, 0x80
    je      .loop
    inc     rax
    jmp     .loop
.done:
    RETURN
ENDFUNCTION

; size_t qa_upper_ascii(char *s, size_t length): upper-cases ASCII letters in
; place, in a buffer the caller owns; returns how many it changed.
FUNCTION qa_upper_ascii, 2
    xor     eax, eax
    test    ARG2, ARG2
    jz      .done
.loop:
    movzx   r8d, byte [ARG1]
    lea     r9d, [r8 - 'a']
    cmp     r9d, 25
    ja      .next
    sub     r8d, 32
    mov     [ARG1], r8b
    inc     rax
.next:
    inc     ARG1
    dec     ARG2
    jnz     .loop
.done:
    RETURN
ENDFUNCTION

; int32_t qa_divmod(int32_t a, int32_t b, int32_t *quotient, int32_t *remainder)
; 0 on success, -1 when b is 0 (the out values are then not written).
; idiv needs edx, an argument register on both conventions, so the pointers
; move to r10/r11 and the divisor to ARG1's register first.
FUNCTION qa_divmod, 4
    test    ARG2d, ARG2d
    jz      .by_zero
    mov     r10, ARG3
    mov     r11, ARG4
    mov     eax, ARG1d
    mov     ARG1d, ARG2d
    cdq
    idiv    ARG1d
    mov     [r10], eax
    mov     [r11], edx
    xor     eax, eax
    RETURN
.by_zero:
    mov     eax, -1
    RETURN
ENDFUNCTION

; bool qa_is_even(int64_t x): a C bool, one byte in al.
FUNCTION qa_is_even, 1
    test    ARG1b, 1
    sete    al
    movzx   eax, al
    RETURN
ENDFUNCTION

; int64_t qa_counter_add(int64_t delta): atomically adds to a counter in .data
; and returns the new value (callable from any number of threads).
FUNCTION qa_counter_add, 1
    mov     rax, ARG1
    lock xadd [counter], rax
    add     rax, ARG1
    RETURN
ENDFUNCTION

; void qa_add_f32(const float *a, const float *b, float *sum, size_t count)
; SSE: 4 floats per step, then the rest one at a time.
FUNCTION qa_add_f32, 4
    xor     eax, eax
    mov     r10, ARG4
    and     r10, -4
    jz      .tail
.vector:
    movups  xmm0, [ARG1 + rax * 4]
    movups  xmm1, [ARG2 + rax * 4]
    addps   xmm0, xmm1
    movups  [ARG3 + rax * 4], xmm0
    add     rax, 4
    cmp     rax, r10
    jb      .vector
.tail:
    cmp     rax, ARG4
    jae     .done
    movss   xmm0, [ARG1 + rax * 4]
    addss   xmm0, [ARG2 + rax * 4]
    movss   [ARG3 + rax * 4], xmm0
    inc     rax
    jmp     .tail
.done:
    RETURN
ENDFUNCTION

; int64_t qa_sum_squares(const int64_t *values, size_t count): through
; QaHelpers' qa_helper_square, a hidden function of a linked static library
; (a direct call; the helper is exported by no OS).
FUNCTION qa_sum_squares, 2, rbx, r12, r13
    sub     rsp, SHADOW_SPACE           ; 3 pushes: already 16-aligned
    mov     rbx, ARG1
    mov     r12, ARG2
    xor     r13d, r13d
    test    r12, r12
    jz      .done
.loop:
    mov     ARG1, [rbx]
    call    qa_helper_square
    add     r13, rax
    add     rbx, 8
    dec     r12
    jnz     .loop
.done:
    mov     rax, r13
    add     rsp, SHADOW_SPACE
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
