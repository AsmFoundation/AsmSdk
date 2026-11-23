; main.asm - the x64 RIDs of SectionlessProgram: no section directive and no include, so the
; code below starts in the section ImplicitUsings names. sl_mul is in the Sectionless static
; library; NASM needs the extern (a directive, not a section).
extern sl_mul

; int main(void): sl_mul(6, 7), so the program exits with status 42. A call needs rsp 16-byte
; aligned, and the return address left it 8 bytes off: the extra 8 align it. SHADOW_SPACE is
; the 32 bytes a callee may use on Windows (0 elsewhere). sl_mul is hidden in this image, so the
; call is direct, through no PLT.
FUNCTION main
    sub     rsp, SHADOW_SPACE + 8
    mov     ARG1d, 6
    mov     ARG2d, 7
    call    sl_mul
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION
