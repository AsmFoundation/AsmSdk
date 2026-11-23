; strings.asm - ApiProto's x64 RIDs: the length of a NUL-terminated string (a const char*).

PROTO size_t name_length(const char* name)
FUNCTION name_length, 1
    xor     eax, eax
.next:
    cmp     byte [ARG1 + rax], 0
    je      .done
    inc     rax
    jmp     .next
.done:
    RETURN
ENDFUNCTION
