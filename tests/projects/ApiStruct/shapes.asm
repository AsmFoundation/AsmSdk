; shapes.asm - ApiStruct's x64 RIDs: functions taking structs by pointer, the structs from an
; include file. The constants (rect.max, point.y, rect_size) come from the SDK's pre-include.
%include "shapes.inc"

; The layout, checked by the assembler itself: the constants are preprocessor numbers, so %if
; reads them. C puts rect.max at 8 (two int32_t of min before it) and rect is 20 bytes.
%if rect_size != 20 || rect.max != 8 || rect.flags != 16 || point.y != 4
    %error the SDK laid out rect differently from C
%endif

; int32_t rect_area(const rect* r): (max.x - min.x) * (max.y - min.y). r8 is scratch on both
; conventions (a one-argument function: Windows' ARG3, System V's ARG5, unused here).
PROTO int32_t rect_area(const rect* r)
FUNCTION rect_area, 1
    mov     eax, [ARG1 + rect.max + point.x]
    sub     eax, [ARG1 + rect.min + point.x]
    mov     r8d, [ARG1 + rect.max + point.y]
    sub     r8d, [ARG1 + rect.min + point.y]
    imul    eax, r8d
    RETURN
ENDFUNCTION

; void point_set(point* p, int32_t x, int32_t y): writes both fields through the pointer.
PROTO void point_set(point* p, int32_t x, int32_t y)
FUNCTION point_set, 3
    mov     [ARG1 + point.x], ARG2d
    mov     [ARG1 + point.y], ARG3d
    RETURN
ENDFUNCTION
