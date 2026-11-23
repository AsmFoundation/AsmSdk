; halve.asm - ApiProto's x64 RIDs: a function of doubles only, so FUNCTION's count of integer
; arguments is 0 and the PROTO check must not count the double.

PROTO double halve(double value)
FUNCTION halve, 0
    mulsd   xmm0, [half]
    RETURN
ENDFUNCTION

; After the function: FUNCTION starts in the section the source named last.
section .rodata
half:   dq 0.5
