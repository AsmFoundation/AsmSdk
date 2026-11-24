; kinds.asm - ApiStruct's x64 RIDs: one struct per kind of field the SDK lays out.
%include "shapes.inc"

; Arrays of scalars and a bool: h at 0 (32 bytes), length at 32, block at 40 (64 bytes), final at
; 104, and 112 in all (the tail padding keeps an array of them aligned to 8).
STRUCT hash_state, uint32_t h[8], uint64_t length, uint8_t block[64], bool final
; A function pointer field and a pointer to the struct itself: cmp 0, context 8, next 16.
STRUCT sorter, int32_t (*cmp)(int32_t, int32_t), void* context, sorter* next
; Padding between fields: flag 0, data 8 (aligned up), count 16, tail 20, tag 22, 24 in all.
STRUCT mixed, bool flag, const uint8_t* data, uint32_t count, uint16_t tail, char tag

%if hash_state_size != 112 || hash_state.final != 104 || mixed.tag != 22 || sorter.next != 16
    %error the SDK laid out the kinds differently from C
%endif

; uint64_t state_sum(const hash_state* s): h[0] + ... + h[7] + length + block[63] + final.
PROTO uint64_t state_sum(const hash_state* s)
FUNCTION state_sum, 1
    xor     eax, eax
    %assign i 0
    %rep 8
        mov     r8d, [ARG1 + hash_state.h + 4 * i]
        add     rax, r8
        %assign i i + 1
    %endrep
    add     rax, [ARG1 + hash_state.length]
    movzx   r8d, byte [ARG1 + hash_state.block + 63]
    add     rax, r8
    movzx   r8d, byte [ARG1 + hash_state.final]
    add     rax, r8
    RETURN
ENDFUNCTION

; int32_t sorter_call(const sorter* s, int32_t a, int32_t b): s->cmp(a, b), the function
; pointer read from the struct (as ApiProto's apply calls one passed directly).
PROTO int32_t sorter_call(const sorter* s, int32_t a, int32_t b)
FUNCTION sorter_call, 3
    ; rsp is 16n+8 at entry: the extra 8 align the call; SHADOW_SPACE is cmp's on Windows.
    sub     rsp, SHADOW_SPACE + 8
    mov     rax, [ARG1 + sorter.cmp]
    ; The arguments move down one register, each read before it is written (as in apply).
    mov     ARG1d, ARG2d
    mov     ARG2d, ARG3d
    call    rax
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; uint32_t mixed_get(const mixed* m): count + tail + data[0] + tag when flag is set, else 0.
PROTO uint32_t mixed_get(const mixed* m)
FUNCTION mixed_get, 1
    xor     eax, eax
    cmp     byte [ARG1 + mixed.flag], 0
    je      .done
    mov     eax, [ARG1 + mixed.count]
    movzx   r8d, word [ARG1 + mixed.tail]
    add     eax, r8d
    mov     r9, [ARG1 + mixed.data]
    movzx   r8d, byte [r9]
    add     eax, r8d
    movzx   r8d, byte [ARG1 + mixed.tag]
    add     eax, r8d
.done:
    RETURN
ENDFUNCTION

; size_t struct_sizes(size_t* out): every struct's size constant, in out[0..4] (point, rect,
; hash_state, sorter, mixed), for the C# side to compare with sizeof; returns 5.
PROTO size_t struct_sizes(size_t* out)
FUNCTION struct_sizes, 1
    mov     qword [ARG1 + 0], point_size
    mov     qword [ARG1 + 8], rect_size
    mov     qword [ARG1 + 16], hash_state_size
    mov     qword [ARG1 + 24], sorter_size
    mov     qword [ARG1 + 32], mixed_size
    mov     eax, 5
    RETURN
ENDFUNCTION
