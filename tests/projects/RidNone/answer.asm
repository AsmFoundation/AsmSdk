; answer.asm - RidNone's code, built for the build machine's RID (x64 machines: this file).
%include "asmsdk.inc"
section .text
FUNCTION answer
    mov     eax, 42
    RETURN
ENDFUNCTION
