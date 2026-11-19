; Windows x64 (qualifier "win").
default rel
%include "asmsdk.inc"
section .text
HIDDEN_FUNCTION osname
    mov     eax, 11
    RETURN
ENDFUNCTION
