; musl (qualifier "linux-musl", closer than "linux" for linux-musl-x64).
default rel
%include "asmsdk.inc"
section .text
HIDDEN_FUNCTION osname
    mov     eax, 13
    RETURN
ENDFUNCTION
section .note.GNU-stack noalloc noexec nowrite progbits
