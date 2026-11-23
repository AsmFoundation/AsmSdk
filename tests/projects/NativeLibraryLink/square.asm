; square.asm - nl_square calls static_mul from libhellostatic.a, which the project links
; through a NativeLibrary item. Without the archive the link still succeeds (a shared library
; may leave a symbol for the loader to find), but static_mul is then an undefined symbol of
; libnativelink.so: the test checks that it is defined in it.

extern static_mul

; int32_t nl_square(int32_t x)
FUNCTION nl_square, 1
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG2d, ARG1d
    call    static_mul PLT
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
