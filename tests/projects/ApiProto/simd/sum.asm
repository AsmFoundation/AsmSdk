; sum.asm - ApiProto's x64 RIDs, in a folder (the bindings nest by it): the byte sum of a buffer.
; fast: a short loop that never blocks, so its binding may skip the GC transition.

PROTO fast uint32_t sum_bytes(const uint8_t* data, size_t len)
FUNCTION sum_bytes, 2
    xor     eax, eax
    test    ARG2, ARG2
    jz      .done
.next:
    ; r9 is free here on every OS: ARG4 on Windows, ARG6 elsewhere, and two arguments only.
    movzx   r9d, byte [ARG1]
    add     eax, r9d
    inc     ARG1
    dec     ARG2
    jnz     .next
.done:
    RETURN
ENDFUNCTION
