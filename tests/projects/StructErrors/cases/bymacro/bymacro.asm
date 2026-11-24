; bymacro.asm - StructErrors: a STRUCT written by a macro. The SDK reads STRUCT lines from the
; text before assembling, and a macro body is not assembled where it is written, so it gives the
; source no constants for "made"; the STRUCT macro says so when line 8 expands it.
%macro DECLARE 1
STRUCT %1, uint32_t a
%endmacro

DECLARE made
