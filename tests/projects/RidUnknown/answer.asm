; Never assembled: the build stops at ASM0011 first.
%include "asmsdk.inc"
section .text
FUNCTION answer
    mov     eax, 42
    RETURN
ENDFUNCTION
