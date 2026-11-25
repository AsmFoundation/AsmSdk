; geometry.asm - BindingsPackage's x64 RIDs: a struct the C# side fills, read here through the
; offset constants the SDK lays out from the STRUCT line (point.x, point.y, point_size).

STRUCT point, int32_t x, int32_t y

%if point_size != 8 || point.y != 4
    %error the SDK laid out point differently from C
%endif

; int32_t point_sum(const point* p): p->x + p->y.
PROTO int32_t point_sum(const point* p)
FUNCTION point_sum, 1
    mov     eax, [ARG1 + point.x]
    add     eax, [ARG1 + point.y]
    RETURN
ENDFUNCTION
