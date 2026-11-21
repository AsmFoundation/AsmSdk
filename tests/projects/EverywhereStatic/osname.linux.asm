; Linux x64 with glibc (qualifier "linux"; musl has a closer file).
default rel
%include "asmsdk.inc"
section .text
HIDDEN_FUNCTION osname
    mov     eax, 12
    RETURN
ENDFUNCTION
section .note.GNU-stack noalloc noexec nowrite progbits
