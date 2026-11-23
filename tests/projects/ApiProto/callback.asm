; callback.asm - ApiProto's x64 RIDs: calls a function pointer, op(a, b).

PROTO int32_t apply(int32_t (*op)(int32_t, int32_t), int32_t a, int32_t b)
FUNCTION apply, 3
    ; rsp is 16n+8 at entry: the extra 8 align the call; SHADOW_SPACE is op's on Windows.
    sub     rsp, SHADOW_SPACE + 8
    mov     rax, ARG1
    ; The arguments move down one register: ARG2 is read before ARG1 is written, and ARG3
    ; before ARG2, on both conventions (rcx <- edx, edx <- r8d; edi <- esi, esi <- edx).
    mov     ARG1d, ARG2d
    mov     ARG2d, ARG3d
    call    rax
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
