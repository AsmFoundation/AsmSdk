; ProgramExports: a program that exports plugin_api through its ExportsFile (program.map).
; main and not_listed are FUNCTIONs too, and the file leaves them unexported.
default rel
%include "asmsdk.inc"

section .text
FUNCTION main
    xor     eax, eax
    RETURN
ENDFUNCTION

FUNCTION plugin_api
    mov     eax, 7
    RETURN
ENDFUNCTION

FUNCTION not_listed
    mov     eax, 8
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
