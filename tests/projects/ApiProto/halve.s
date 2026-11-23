// halve.s - ApiProto's ARM64 RIDs: value / 2, as halve.asm.

PROTO double halve(double value)
FUNCTION halve
    fmov    d1, #0.5
    fmul    d0, d0, d1
    ret
ENDFUNCTION halve
