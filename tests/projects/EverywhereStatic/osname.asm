; Generic x64: every x64 RID without a closer osname.* file (osx-x64 here).
default rel
%include "asmsdk.inc"
section .text
HIDDEN_FUNCTION osname
    mov     eax, 10
    RETURN
ENDFUNCTION
