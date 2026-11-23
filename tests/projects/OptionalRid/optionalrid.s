// optionalrid.s - OptionalRid's one RID, linux-musl-arm64 (GNU syntax for llvm-mc).

// int32_t or_answer(void): 42.
FUNCTION or_answer
    mov     w0, #42
    ret
ENDFUNCTION or_answer
