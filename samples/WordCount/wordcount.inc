; =============================================================================
; wordcount.inc - the Counts record shared by main.asm and count.asm
; (WordCount sample).
; =============================================================================

%ifndef WORDCOUNT_INC
%define WORDCOUNT_INC

; struct Counts: one file's (or the total's) running counts.
struc Counts
    .lines:     resq 1          ; newline bytes seen
    .words:     resq 1          ; words started (a non-space byte after a space)
    .bytes:     resq 1          ; bytes seen
    .space:     resq 1          ; 1 when the last byte seen was whitespace, or
                                ; nothing was seen yet: a non-space byte that
                                ; follows starts a word. Carried across reads, so
                                ; a word split between two reads counts once.
endstruc

; void count_buffer(struct Counts *counts, const uint8_t *data, size_t length);
; Adds data to the counts. Whitespace is C's isspace() in the C locale:
; space, \t, \n, \v, \f and \r. Needs SSE2 and POPCNT (x86-64-v2).
%ifndef WORDCOUNT_COUNT_ASM
extern count_buffer
%endif

%endif ; WORDCOUNT_INC
