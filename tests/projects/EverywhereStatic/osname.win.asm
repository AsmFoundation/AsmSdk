; Windows x64 (qualifier "win").
default rel
%include "asmsdk.inc"
section .text
FUNCTION osname
    mov     eax, 11
    RETURN
ENDFUNCTION
