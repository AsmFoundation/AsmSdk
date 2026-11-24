// bymacro.s - StructErrors: the GNU-syntax twin of bymacro.asm (linux-arm64). The STRUCT line is
// inside a .macro body, which the SDK skips, so the STRUCT macro stops when line 8 expands it.
.macro DECLARE name
STRUCT \name, uint32_t a
.endm

// Expanded here: "made" has no constants.
DECLARE made
