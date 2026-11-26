; chain.asm - the x64 code of Chain (win-x64, linux-x64, linux-musl-x64, osx-x64).
;
; The SDK puts its platform layer in front of this file (ImplicitUsings, on by default):
; HIDDEN_FUNCTION, ARG1 .. ARG6, SHADOW_SPACE, RETURN and ENDFUNCTION.
%include "Shared.inc"               ; extern mul_ints, from the referenced Shared's include/ folder

; int32_t square_ints(int32_t a): mul_ints(a, a), from Shared, so a program that links this
; library links Shared too.
; HIDDEN_FUNCTION: global for the code that links this library, exported by no image.
; The return address left rsp 8 bytes off a 16-byte boundary; the extra 8 align it for the call.
HIDDEN_FUNCTION square_ints, 1
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG2d, ARG1d
    call    mul_ints
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
