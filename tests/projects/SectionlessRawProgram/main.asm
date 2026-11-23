; main.asm - the x64 RIDs of SectionlessRawProgram: ImplicitUsings is disabled, so the source
; includes asmsdk.inc and says "default rel" itself, and names no section before its functions.
; raw_mul is in the SectionlessRaw static library; NASM needs the extern.
%include "asmsdk.inc"
default rel
extern raw_mul

; int main(void): raw_mul(6, seven()), so the program exits with status 42. The call through
; seven_ptr needs seven's label as a symbol (the pointer in .data is relocated against it),
; which NASM's COFF output wrote undefined for a label before any section directive. A call
; needs rsp 16-byte aligned, and the return address left it 8 bytes off: the extra 8 align it.
FUNCTION main
    sub     rsp, SHADOW_SPACE + 8
    call    [seven_ptr]
    mov     ARG2d, eax
    mov     ARG1d, 6
    call    raw_mul
    add     rsp, SHADOW_SPACE + 8
    RETURN
ENDFUNCTION

; int seven(void): 7, a file-local function.
LOCAL_FUNCTION seven
    mov     eax, 7
    RETURN
ENDFUNCTION

; A writable section: a pointer there is fixed up at load time on every OS (in read-only data
; it would be a text relocation on macOS, ASM0081).
section .data
seven_ptr:  dq seven
