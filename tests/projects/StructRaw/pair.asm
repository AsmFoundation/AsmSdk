; pair.asm - StructRaw's x64 RID, ImplicitUsings off: the layer included by hand, the STRUCT
; constants still from the SDK's pre-include.
%include "asmsdk.inc"
section .text

STRUCT pair, uint8_t tag, uint64_t value

%if pair.value != 8 || pair_size != 16
    %error the SDK laid out pair differently from C
%endif

; uint64_t pair_sum(const pair* p): tag + value.
PROTO uint64_t pair_sum(const pair* p)
FUNCTION pair_sum, 1
    movzx   eax, byte [ARG1 + pair.tag]
    add     rax, [ARG1 + pair.value]
    RETURN
ENDFUNCTION
