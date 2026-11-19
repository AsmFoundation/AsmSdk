; =============================================================================
; count.asm - the counting core of wordcount: lines, words and bytes, 16 bytes
; per step with SSE2 (WordCount sample).
;
; For each 16-byte block, two byte-wise compares give a 16-bit mask of the
; newline bytes and one of the whitespace bytes (pmovmskb moves one bit per
; byte into a general register). Then:
;   lines += popcount(newline mask)
;   words += popcount(starts), starts = non-space bytes whose previous byte is
;            a space: ~ws & ((ws << 1) | space_before_block)
; so a block costs a handful of instructions however many words it holds, and
; no byte is looked at one by one except in the last partial block.
; =============================================================================

default rel
bits 64

%define WORDCOUNT_COUNT_ASM             ; we define count_buffer
%include "wordcount.inc"

section .rodata
align 16
newlines:   times 16 db 10
spaces:     times 16 db ' '
nines:      times 16 db 9               ; \t is 9; \t \n \v \f \r are 9-13
fours:      times 16 db 4

; -----------------------------------------------------------------------------
; CLASSIFY - the newline and whitespace masks of one 16-byte block.
; in:  xmm0 = the block; xmm5-xmm8 = newlines, spaces, nines, fours
; out: eax = newline mask, ecx = whitespace mask (bit i = byte i)
; Clobbers xmm1-xmm4.
; The range test for 9..13 has no unsigned byte compare in SSE2, so it
; subtracts 9 (bytes below 9 wrap to 247-255) and checks that the result is
; unchanged by min(result, 4): only 0..4, i.e. bytes 9..13, are.
; -----------------------------------------------------------------------------
%macro CLASSIFY 0
    movdqa  xmm1, xmm0
    pcmpeqb xmm1, xmm5                  ; byte == '\n'
    pmovmskb eax, xmm1
    movdqa  xmm2, xmm0
    pcmpeqb xmm2, xmm6                  ; byte == ' '
    movdqa  xmm3, xmm0
    psubb   xmm3, xmm7                  ; byte - 9, wrapping
    movdqa  xmm4, xmm3
    pminub  xmm4, xmm8                  ; min(byte - 9, 4)
    pcmpeqb xmm3, xmm4                  ; byte - 9 <= 4: \t \n \v \f \r
    por     xmm2, xmm3
    pmovmskb ecx, xmm2
%endmacro

; -----------------------------------------------------------------------------
; ADD_BLOCK - adds one classified block to the running counts.
; in:  eax = newline mask, ecx = whitespace mask, r10d = space before the block
; in/out: r8 = lines, r9 = words
; Clobbers eax, r11.
; -----------------------------------------------------------------------------
%macro ADD_BLOCK 0
    popcnt  eax, eax
    add     r8, rax                     ; lines
    mov     r11d, ecx
    shl     r11d, 1
    or      r11d, r10d                  ; bit i: byte i-1 is a space
    mov     eax, ecx
    not     eax
    and     eax, r11d                   ; non-space after a space: a word starts
    and     eax, 0xFFFF                 ; bit 16 is the shifted-out byte 15
    popcnt  eax, eax
    add     r9, rax                     ; words
%endmacro

section .text

; -----------------------------------------------------------------------------
; count_buffer - adds a buffer to the counts.
; @param  rdi  struct Counts * (updated)
; @param  rsi  data
; @param  rdx  length in bytes (0 is fine)
; @returns nothing
; A leaf function: the last partial block is padded in the 128-byte red zone
; below rsp, which the System V ABI keeps for leaf functions, so no stack
; frame is needed. Padding with spaces adds no newline and starts no word.
; -----------------------------------------------------------------------------
global count_buffer:function
count_buffer:
    mov     r8, [rdi + Counts.lines]
    mov     r9, [rdi + Counts.words]
    add     [rdi + Counts.bytes], rdx
    mov     r10, [rdi + Counts.space]
    movdqa  xmm5, [newlines]
    movdqa  xmm6, [spaces]
    movdqa  xmm7, [nines]
    movdqa  xmm8, [fours]

.block:
    cmp     rdx, 16
    jb      .tail
    movdqu  xmm0, [rsi]
    CLASSIFY
    ADD_BLOCK
    shr     ecx, 15                     ; byte 15's whitespace bit
    mov     r10d, ecx                   ; becomes the next block's "space before"
    add     rsi, 16
    sub     rdx, 16
    jmp     .block

.tail:
    test    rdx, rdx
    jz      .store
    movdqu  [rsp - 16], xmm6            ; 16 spaces in the red zone
    xor     ecx, ecx
.copy:
    mov     al, [rsi + rcx]
    mov     [rsp - 16 + rcx], al
    inc     rcx
    cmp     rcx, rdx
    jb      .copy
    movdqu  xmm0, [rsp - 16]
    CLASSIFY
    mov     esi, ecx                    ; keep the whitespace mask: ADD_BLOCK reads ecx
    ADD_BLOCK
    ; The carried bit is the last real byte's, not the padding's: a word that
    ; continues into the next read must not count twice.
    lea     ecx, [rdx - 1]
    xor     r10d, r10d
    bt      esi, ecx
    setc    r10b

.store:
    mov     [rdi + Counts.lines], r8
    mov     [rdi + Counts.words], r9
    mov     [rdi + Counts.space], r10
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
