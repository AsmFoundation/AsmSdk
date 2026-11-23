; main.asm - ApiProgram: a program whose plugin_add has a PROTO line, as the function a plugin
; host offers its plugins would. main is the program's own entry and has none.

PROTO int32_t plugin_add(int32_t a, int32_t b)
FUNCTION plugin_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION

; int main(void): exits with status 42. The test never runs it: what it checks is the .NET
; project's build.
FUNCTION main
    mov     eax, 42
    RETURN
ENDFUNCTION
