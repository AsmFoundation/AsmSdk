; main.asm - CliGraph's ChainProg for the x64 RIDs: square_ints from the static library Chain.
%include "Chain.inc"                ; extern square_ints, from Chain's include/ folder

; int main(void): square_ints(6) + 6, so the program exits with status 42. Chain's square_ints
; calls Shared's mul_ints, which this program links without naming Shared.
; The return address left rsp 8 bytes off a 16-byte boundary; the extra 8 align it for the call.
FUNCTION main
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG1d, 6
    call    square_ints
    add     eax, 6
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
