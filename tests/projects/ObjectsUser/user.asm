; ObjectsUser: user_seven() calls obj_seven() from ObjectsLib's object file.
default rel
%include "asmsdk.inc"
extern obj_seven

section .text
FUNCTION user_seven
    sub     rsp, SHADOW_SPACE + 8
    call    obj_seven
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
