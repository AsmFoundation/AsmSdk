; NASM source named for WebAssembly: no RID can ever build it (ASM0021).
section .text
global kernel
kernel:
    mov     eax, 42
    ret
