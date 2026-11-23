; add.asm - ApiProto's x64 RIDs: a PROTO line beside the FUNCTION it describes.

PROTO int32_t add_ints(int32_t a, int32_t b)
FUNCTION add_ints, 2
    lea     eax, [ARG1 + ARG2]
    RETURN
ENDFUNCTION
