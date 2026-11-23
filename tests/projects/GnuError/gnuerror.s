// gnuerror.s - one error on purpose, on line 4 (column 5): "bogus" is no ARM64 instruction.
FUNCTION gnu_answer
    mov     w0, #42
    bogus   w0, w1
    ret
ENDFUNCTION gnu_answer
