; =============================================================================
; main.asm - wordcount: counts lines, words and bytes like wc (WordCount
; sample, an executable on glibc).
;
;     wordcount [-lwc] [file ...]
;
;   -l, -w, -c  print only lines, words or bytes (any combination, also as
;               -lw); without options, all three, in that order
;   --          ends the options; "-" as a file name reads standard input
;   no files    reads standard input, and prints no name
;
; Output: one line per file, each count right-aligned in 8 columns, then the
; name, and a "total" line when there were several files. Errors go to
; standard error in the usual "wordcount: name: reason" form (warn/warnx).
; Exit status: 0 success, 1 a file could not be read (the others are still
; counted), 2 a usage error, 3 the CPU lacks POPCNT (x86-64-v2 required).
;
; It shows an assembly program that uses libc the ordinary way: open, read,
; close and printf through the PLT; warn and warnx, which the SDK's libc stub
; does not list, added to it with AsmLibcSymbol items in WordCount.asmproj;
; and stderr, a libc *data* object, read through the GOT.
; =============================================================================

default rel
bits 64

%include "wordcount.inc"

extern open, read, close, printf, fputs, warn, warnx, stderr

%define O_RDONLY            0
%define READ_SIZE           65536
%define SHOW_LINES          1
%define SHOW_WORDS          2
%define SHOW_BYTES          4
%define CPUID_ECX_POPCNT    23

section .rodata
fmt_count:      db "%8lu", 0
fmt_name:       db " %s", 10, 0
newline:        db 10, 0
name_total:     db "total", 0
msg_option:     db "unknown option -%c", 0
msg_long:       db "unknown option %s", 0
msg_cpu:        db "this CPU has no POPCNT instruction (x86-64-v2 is required)", 0
usage:          db "usage: wordcount [-lwc] [file ...]", 10, 0
fmt_warn_name:  db "%s", 0

section .bss
alignb 16
buffer:         resb READ_SIZE
file_counts:    resb Counts_size
total_counts:   resb Counts_size

section .text

; -----------------------------------------------------------------------------
; main - parses the options, counts each file, prints the counts.
; @param  edi  argc
; @param  rsi  argv
; @returns eax = the exit status (see the file header)
; Register use across the libc calls (all callee-saved):
;   rbx = files counted, r12 = the next argv entry, r13 = which counts to show,
;   r14 = the exit status, r15 = 1 once a file argument was seen.
; main is entered with rsp = 16n + 8; six pushes make it 16n + 8 + 48 = 16m + 8,
; so one more 8-byte slot aligns the stack for every call.
; -----------------------------------------------------------------------------
global main:function
main:
    push    rbx
    push    rbp
    push    r12
    push    r13
    push    r14
    push    r15
    sub     rsp, 8

    ; count_buffer needs POPCNT; say so rather than die of SIGILL.
    mov     r8, rsi                     ; cpuid clobbers rbx, rcx, rdx
    mov     eax, 1
    cpuid
    mov     rsi, r8
    bt      ecx, CPUID_ECX_POPCNT
    jc      .cpu_ok
    lea     rdi, [msg_cpu]
    xor     eax, eax
    call    warnx wrt ..plt
    mov     r14d, 3
    jmp     .exit
.cpu_ok:
    xor     ebx, ebx
    lea     r12, [rsi + 8]              ; argv[1]
    xor     r13d, r13d
    xor     r14d, r14d
    xor     r15d, r15d

    ; ---- options: leading arguments that start with '-' (but not "-") ----
.option:
    mov     rbp, [r12]
    test    rbp, rbp
    jz      .options_done               ; end of argv
    cmp     byte [rbp], '-'
    jne     .options_done
    movzx   eax, byte [rbp + 1]
    test    eax, eax
    jz      .options_done               ; "-": a file name (standard input)
    cmp     eax, '-'
    jne     .letters
    cmp     byte [rbp + 2], 0
    jne     .bad_long                   ; "--x": there are no long options
    add     r12, 8                      ; "--": the rest are file names
    jmp     .options_done
.letters:
    inc     rbp
.letter:
    movzx   eax, byte [rbp]
    test    eax, eax
    jz      .next_option
    mov     ecx, SHOW_LINES
    cmp     eax, 'l'
    je      .set
    mov     ecx, SHOW_WORDS
    cmp     eax, 'w'
    je      .set
    mov     ecx, SHOW_BYTES
    cmp     eax, 'c'
    jne     .bad_option
.set:
    or      r13d, ecx
    inc     rbp
    jmp     .letter
.next_option:
    add     r12, 8
    jmp     .option
.bad_long:
    lea     rdi, [msg_long]
    mov     rsi, rbp                    ; the whole argument
    jmp     .complain
.bad_option:
    lea     rdi, [msg_option]
    movzx   esi, byte [rbp]             ; the letter
.complain:
    xor     eax, eax
    call    warnx wrt ..plt
    lea     rdi, [usage]
    mov     rax, [rel stderr wrt ..got] ; &stderr, from the GOT
    mov     rsi, [rax]                  ; stderr itself: a FILE *
    call    fputs wrt ..plt
    mov     r14d, 2
    jmp     .exit

.options_done:
    test    r13d, r13d
    jnz     .files
    mov     r13d, SHOW_LINES | SHOW_WORDS | SHOW_BYTES

    ; ---- files ----
.files:
    mov     rbp, [r12]
    test    rbp, rbp
    jz      .files_done
    mov     r15d, 1
    mov     rdi, rbp
    call    count_file
    add     r12, 8
    jmp     .files

.files_done:
    test    r15d, r15d
    jnz     .totals
    xor     edi, edi                    ; no file arguments: standard input, no name
    call    count_file
    jmp     .exit

.totals:
    cmp     rbx, 1
    jbe     .exit
    lea     rdi, [total_counts]
    lea     rsi, [name_total]
    call    print_counts

.exit:
    mov     eax, r14d
    add     rsp, 8
    pop     r15
    pop     r14
    pop     r13
    pop     r12
    pop     rbp
    pop     rbx
    ret

; -----------------------------------------------------------------------------
; count_file - counts one file (or standard input), prints its line, and adds
; it to the totals. On failure, warns and sets the exit status to 1.
; @param  rdi  the name; "-" or null reads standard input (null prints no name)
; Uses main's callee-saved registers: rbx (files counted, incremented),
; r13 (what to show), r14 (exit status). Preserves r12 and r15.
; Stack: entered with rsp = 16n + 8; three pushes give 16m, aligned.
; -----------------------------------------------------------------------------
count_file:
    push    rbp
    push    r12
    push    r15
    mov     rbp, rdi                    ; the name (may be null)
    xor     r12d, r12d                  ; fd 0, standard input
    xor     r15d, r15d                  ; 1 when we opened the file
    test    rdi, rdi
    jz      .opened
    cmp     word [rdi], '-'             ; "-" followed by its terminating NUL
    je      .opened
    mov     esi, O_RDONLY
    xor     eax, eax                    ; open is variadic: no vector arguments
    call    open wrt ..plt
    test    eax, eax
    js      .failed
    mov     r12d, eax
    mov     r15d, 1
.opened:
    lea     rdi, [file_counts]          ; zero the counts, "space before" = 1
    xor     eax, eax
    mov     [rdi + Counts.lines], rax
    mov     [rdi + Counts.words], rax
    mov     [rdi + Counts.bytes], rax
    mov     qword [rdi + Counts.space], 1
.read:
    mov     edi, r12d
    lea     rsi, [buffer]
    mov     edx, READ_SIZE
    call    read wrt ..plt
    test    rax, rax
    jz      .eof
    js      .read_failed
    lea     rdi, [file_counts]
    lea     rsi, [buffer]
    mov     rdx, rax
    call    count_buffer
    jmp     .read

.read_failed:
    call    warn_name                   ; errno is read's
    test    r15d, r15d
    jz      .done
    mov     edi, r12d
    call    close wrt ..plt
    jmp     .done

.eof:
    test    r15d, r15d
    jz      .print
    mov     edi, r12d
    call    close wrt ..plt
.print:
    lea     rdi, [file_counts]
    mov     rsi, rbp
    call    print_counts
    inc     rbx
    lea     rsi, [file_counts]          ; totals += this file
    lea     rdi, [total_counts]
    mov     rax, [rsi + Counts.lines]
    add     [rdi + Counts.lines], rax
    mov     rax, [rsi + Counts.words]
    add     [rdi + Counts.words], rax
    mov     rax, [rsi + Counts.bytes]
    add     [rdi + Counts.bytes], rax
    jmp     .done

.failed:
    call    warn_name                   ; errno is open's
.done:
    pop     r15
    pop     r12
    pop     rbp
    ret

; -----------------------------------------------------------------------------
; warn_name - "wordcount: <name>: <strerror(errno)>" on stderr; exit status 1.
; @param  rbp  the name (count_file's)
; Entered with rsp = 16m + 8 from count_file (16m), so one push aligns the call.
; -----------------------------------------------------------------------------
warn_name:
    push    rbx
    lea     rdi, [fmt_warn_name]
    mov     rsi, rbp
    test    rsi, rsi
    jnz     .named
    lea     rsi, [stdin_name]
.named:
    xor     eax, eax
    call    warn wrt ..plt
    mov     r14d, 1
    pop     rbx
    ret

; -----------------------------------------------------------------------------
; print_counts - prints the selected counts and the name.
; @param  rdi  struct Counts *
; @param  rsi  the name, or null for none
; Uses r13 (what to show). Entered with rsp = 16n + 8; three pushes give
; 16m, aligned for printf.
; -----------------------------------------------------------------------------
print_counts:
    push    rbx
    push    r12
    push    r14
    mov     rbx, rdi
    mov     r12, rsi
    test    r13d, SHOW_LINES
    jz      .words
    lea     rdi, [fmt_count]
    mov     rsi, [rbx + Counts.lines]
    xor     eax, eax
    call    printf wrt ..plt
.words:
    test    r13d, SHOW_WORDS
    jz      .bytes
    lea     rdi, [fmt_count]
    mov     rsi, [rbx + Counts.words]
    xor     eax, eax
    call    printf wrt ..plt
.bytes:
    test    r13d, SHOW_BYTES
    jz      .name
    lea     rdi, [fmt_count]
    mov     rsi, [rbx + Counts.bytes]
    xor     eax, eax
    call    printf wrt ..plt
.name:
    test    r12, r12
    jz      .no_name
    lea     rdi, [fmt_name]
    mov     rsi, r12
    xor     eax, eax
    call    printf wrt ..plt
    jmp     .done
.no_name:
    lea     rdi, [newline]
    xor     eax, eax
    call    printf wrt ..plt
.done:
    pop     r14
    pop     r12
    pop     rbx
    ret

section .rodata
stdin_name:     db "-", 0

section .note.GNU-stack noalloc noexec nowrite progbits
