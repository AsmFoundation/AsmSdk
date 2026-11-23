; deps.asm - reads an include file from outside the project and a binary blob, the two kinds
; of input that DependencyInputs.asmproj declares (a None .inc item, CustomAdditionalCompileInputs).
%include "outside.inc"              ; DEP_ANSWER, from ../SharedIncludes

section .rodata
blob:       incbin "blob.bin"
blob_end:

section .text

; int32_t dep_answer(void): DEP_ANSWER from outside.inc.
FUNCTION dep_answer, 0
    mov     eax, DEP_ANSWER
    RETURN
ENDFUNCTION

; int32_t dep_blob_size(void): the size of blob.bin, as assembled.
FUNCTION dep_blob_size, 0
    mov     eax, blob_end - blob
    RETURN
ENDFUNCTION
