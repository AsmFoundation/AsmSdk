; shapes.inc - the structs both x64 sources of ApiStruct include, from the project's include
; folder: NASM finds it there through -I, and the SDK, reading the STRUCT lines before
; assembling, finds it the same way.

STRUCT point, int32_t x, int32_t y
STRUCT rect, point min, point max, uint32_t flags
