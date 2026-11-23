; bare.asm - code that needs the platform layer, in a project that disabled ImplicitUsings:
; NASM must reject FUNCTION, a macro it was never given.
FUNCTION off_add, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
