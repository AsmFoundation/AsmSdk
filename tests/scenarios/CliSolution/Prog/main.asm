; main.asm - CliSolution's program for the x64 RIDs: mul_ints from the static library Shared, through its include.
%include "Shared.inc"               ; extern mul_ints, from Shared's include/ folder

; int main(void): mul_ints(6, 7), so the program exits with status 42.
; The return address left rsp 8 bytes off a 16-byte boundary; the extra 8 align it for the call.
FUNCTION main
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG1d, 6
    mov     ARG2d, 7
    call    mul_ints
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
