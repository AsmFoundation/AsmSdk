; macro.asm - built only with WarningCase=Macro (WarningSwitches.asmproj): a NASM warning raised
; inside a macro, whose message must also name where the macro is defined.
%macro OVERFLOWING_BYTE 0
    db 300
%endmacro

section .rodata
overflow:
    OVERFLOWING_BYTE
