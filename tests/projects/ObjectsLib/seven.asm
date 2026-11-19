; ObjectsLib: obj_seven() = 7, a HIDDEN_FUNCTION: ObjectsUser calls it from another object, and
; exports it on no OS.
default rel
%include "asmsdk.inc"

section .text
HIDDEN_FUNCTION obj_seven
    mov     eax, 7
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
