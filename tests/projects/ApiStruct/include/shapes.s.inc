// shapes.s.inc - the same structs for the ARM64 sources (GNU syntax): the same lines as
// shapes.inc, so every RID declares the same structs (the SDK compares them).

STRUCT point, int32_t x, int32_t y
STRUCT rect, point min, point max, uint32_t flags
