; =============================================================================
; main.asm - hexdump: an xxd-style hex dump, freestanding (HexDump sample).
;
;     hexdump [file]        (no file, or "-": standard input)
;
; Prints the same lines as xxd's default format:
;     00000000: 4865 6c6c 6f2c 2077 6f72 6c64 210a       Hello, world!.
; an offset of at least 8 hex digits, 16 bytes as 8 groups of 2, then the
; bytes as text, with "." for anything outside printable ASCII.
; Exit status: 0 success, 1 the file could not be opened, read or written,
; 2 a usage error.
;
; It shows a program with no libc at all: entered at _start by the kernel,
; talking to it with the syscall instruction only. The SDK links it -static,
; so the file is a few kilobytes and needs nothing at run time (no interpreter,
; no DT_NEEDED), and the code does what libc would otherwise do for it:
; buffered output, retrying system calls that a signal interrupted (EINTR),
; writing what a short write left over, and turning errno values into words.
; =============================================================================

default rel
bits 64

%define SYS_READ        0
%define SYS_WRITE       1
%define SYS_CLOSE       3
%define SYS_EXIT_GROUP  231
%define SYS_OPENAT      257
%define AT_FDCWD        -100
%define O_RDONLY        0
%define STDIN           0
%define STDOUT          1
%define STDERR          2
%define EINTR           4
%define ENOENT          2
%define EACCES          13
%define EISDIR          21

%define IN_SIZE         65536           ; a multiple of 16: whole lines per read
%define OUT_SIZE        65536
%define LINE_MAX        84              ; the longest line: 16-digit offset
                                        ; ": " 40 hex " " 16 text "\n" = 76

section .rodata
hex_digits:     db "0123456789abcdef"
name_stdin:     db "-", 0
msg_prefix:     db "hexdump: "
msg_prefix_len  equ $ - msg_prefix
msg_usage:      db "usage: hexdump [file]", 10
msg_usage_len   equ $ - msg_usage
msg_enoent:     db "No such file or directory", 0
msg_eacces:     db "Permission denied", 0
msg_eisdir:     db "Is a directory", 0
msg_error:      db "error ", 0          ; followed by the errno number

section .bss
alignb 16
in_buf:         resb IN_SIZE
out_buf:        resb OUT_SIZE
err_buf:        resb 512

section .text

; -----------------------------------------------------------------------------
; _start - the process entry: the kernel jumps here with argc at [rsp], then
; argv[0..argc-1], a null, the environment.
; Register use (no calls into foreign code, so any register may hold state):
;   r12 = the file descriptor, r13 = the name (for messages),
;   r14 = bytes waiting in in_buf, r15 = the offset of in_buf[0] in the file,
;   rbx = the next free byte of out_buf.
; -----------------------------------------------------------------------------
global _start:function
_start:
    xor     r12d, r12d                  ; standard input
    lea     r13, [name_stdin]
    mov     rax, [rsp]                  ; argc (after the xor: it would clear the flags)
    cmp     rax, 2
    ja      usage_error
    jb      .start                      ; argc 1: no argument
    mov     rdi, [rsp + 16]             ; argv[1]
    mov     r13, rdi
    cmp     word [rdi], '-'             ; "-" and its terminating NUL
    je      .start
    mov     edi, AT_FDCWD
    mov     rsi, r13
    mov     edx, O_RDONLY
.open:
    mov     eax, SYS_OPENAT
    syscall
    cmp     rax, -EINTR
    je      .open
    test    rax, rax
    js      fail_with                   ; rax = -errno
    mov     r12, rax

.start:
    xor     r14d, r14d
    xor     r15d, r15d
    lea     rbx, [out_buf]

.read:
    mov     edi, r12d
    lea     rsi, [in_buf]               ; RIP-relative addressing takes no index
    add     rsi, r14                    ; register, so add the fill level apart
    mov     edx, IN_SIZE
    sub     rdx, r14                    ; room left (r14 < 16, so plenty)
    mov     eax, SYS_READ
    syscall
    cmp     rax, -EINTR
    je      .read
    test    rax, rax
    js      fail_with
    jz      .eof
    add     r14, rax

    ; Dump every whole line in the buffer; keep the partial one for later.
    lea     rsi, [in_buf]
.lines:
    cmp     r14, 16
    jb      .keep
    mov     ecx, 16
    call    dump_line
    add     rsi, 16
    add     r15, 16
    sub     r14, 16
    jmp     .lines
.keep:
    ; Move the 0-15 leftover bytes to the front (rsi points at them).
    lea     rdi, [in_buf]
    mov     rcx, r14
    rep     movsb
    jmp     .read

.eof:
    test    r14, r14
    jz      .flush
    lea     rsi, [in_buf]
    mov     ecx, r14d
    call    dump_line                   ; the last, partial line
.flush:
    call    flush_out
    xor     edi, edi
    jmp     exit

; -----------------------------------------------------------------------------
; dump_line - formats one line into out_buf, flushing first if it might not fit.
; @param  rsi  the line's bytes
; @param  ecx  how many (1-16)
; @param  r15  the line's offset in the file
; @param  rbx  the next free byte of out_buf (advanced)
; Preserves rsi and r14. Clobbers rax, rcx, rdx, rdi, r8-r11.
; -----------------------------------------------------------------------------
dump_line:
    lea     rax, [out_buf + OUT_SIZE - LINE_MAX]
    cmp     rbx, rax
    jbe     .room
    push    rcx
    push    rsi
    call    flush_out
    pop     rsi
    pop     rcx
.room:
    mov     r8d, ecx                    ; byte count
    lea     r9, [hex_digits]

    ; The offset: 8 hex digits, more when it needs them (xxd's %08lx).
    mov     r10d, 8
    mov     rax, r15
    shr     rax, 32
    jz      .digits
    bsr     rax, r15                    ; highest set bit
    shr     eax, 2
    inc     eax
    mov     r10d, eax                   ; ceil((bit + 1) / 4) digits
.digits:
    lea     ecx, [r10 * 4 - 4]          ; the shift of the first digit
.digit:
    mov     rax, r15
    shr     rax, cl
    and     eax, 0xF
    mov     al, [r9 + rax]
    mov     [rbx], al
    inc     rbx
    sub     ecx, 4
    jns     .digit
    mov     word [rbx], ': '
    add     rbx, 2

    ; The hex area: 40 columns, a space after every second byte; missing
    ; bytes of a partial line are blanks, so the text column lines up.
    mov     rdi, rbx
    mov     ecx, 40
    mov     al, ' '
    rep     stosb                       ; blank it first
    xor     ecx, ecx
.hex:
    cmp     ecx, r8d
    jae     .hex_done
    movzx   eax, byte [rsi + rcx]
    mov     edx, ecx
    shr     edx, 1
    lea     edx, [rdx + rdx * 4]        ; 5 columns per pair of bytes
    mov     edi, ecx
    and     edi, 1
    lea     edx, [rdx + rdi * 2]        ; + 2 for the second byte of a pair
    mov     edi, eax
    shr     edi, 4
    mov     dil, [r9 + rdi]
    mov     [rbx + rdx], dil
    and     eax, 0xF
    mov     al, [r9 + rax]
    mov     [rbx + rdx + 1], al
    inc     ecx
    jmp     .hex
.hex_done:
    add     rbx, 40
    mov     byte [rbx], ' '
    inc     rbx

    ; The text: printable ASCII (0x20-0x7e) as itself, anything else as '.'.
    xor     ecx, ecx
.text:
    movzx   eax, byte [rsi + rcx]
    lea     edx, [rax - 0x20]
    cmp     edx, 0x7e - 0x20
    jbe     .printable
    mov     eax, '.'
.printable:
    mov     [rbx], al
    inc     rbx
    inc     ecx
    cmp     ecx, r8d
    jb      .text
    mov     byte [rbx], 10
    inc     rbx
    ret

; -----------------------------------------------------------------------------
; flush_out - writes out_buf[0 .. rbx) to standard output and empties it.
; @param  rbx  the end of the buffered output (reset to out_buf)
; Exits with status 1 on a write error. Clobbers rax, rcx, rdx, rsi, rdi, r11.
; -----------------------------------------------------------------------------
flush_out:
    lea     rsi, [out_buf]
    mov     rdx, rbx
    sub     rdx, rsi
    lea     rbx, [out_buf]
    mov     edi, STDOUT
    jmp     write_all

; -----------------------------------------------------------------------------
; write_all - writes a whole buffer, however many calls the kernel needs.
; A write may take fewer bytes than asked (a pipe, a signal); the rest goes in
; the next call. A signal that arrives first gives EINTR: try again.
; @param  edi  the file descriptor
; @param  rsi  the bytes
; @param  rdx  how many
; Exits with status 1 when the write fails. Clobbers rax, rcx, rdx, rsi, r11.
; -----------------------------------------------------------------------------
write_all:
    test    rdx, rdx
    jz      .done
    mov     eax, SYS_WRITE
    syscall                             ; clobbers rcx and r11
    cmp     rax, -EINTR
    je      write_all
    test    rax, rax
    js      .failed
    add     rsi, rax
    sub     rdx, rax
    jmp     write_all
.done:
    ret
.failed:
    mov     edi, 1
    jmp     exit

; -----------------------------------------------------------------------------
; fail_with - "hexdump: <name>: <reason>" on standard error, then exit 1.
; @param  rax  -errno, from the failed system call
; @param  r13  the name
; -----------------------------------------------------------------------------
fail_with:
    neg     rax
    mov     r8, rax                     ; errno
    lea     rdi, [err_buf]
    lea     rsi, [msg_prefix]
    mov     ecx, msg_prefix_len
    rep     movsb
    mov     rsi, r13
    call    copy_string
    mov     word [rdi], ': '
    add     rdi, 2
    lea     rsi, [msg_enoent]
    cmp     r8, ENOENT
    je      .reason
    lea     rsi, [msg_eacces]
    cmp     r8, EACCES
    je      .reason
    lea     rsi, [msg_eisdir]
    cmp     r8, EISDIR
    je      .reason
    lea     rsi, [msg_error]            ; any other errno: its number
    call    copy_string
    mov     rax, r8
    call    put_decimal
    jmp     .send
.reason:
    call    copy_string
.send:
    mov     byte [rdi], 10
    inc     rdi
    lea     rsi, [err_buf]
    mov     rdx, rdi
    sub     rdx, rsi
    mov     edi, STDERR
    call    write_all
    mov     edi, 1
    jmp     exit

; -----------------------------------------------------------------------------
; copy_string - copies a NUL-terminated string, without the NUL.
; @param  rsi  the string
; @param  rdi  the destination (advanced past the copy)
; The name comes from the command line: it is cut at 400 bytes so that the
; message always fits err_buf. Clobbers rax, rcx, rsi.
; -----------------------------------------------------------------------------
copy_string:
    mov     ecx, 400
.next:
    mov     al, [rsi]
    test    al, al
    jz      .done
    mov     [rdi], al
    inc     rsi
    inc     rdi
    dec     ecx
    jnz     .next
.done:
    ret

; -----------------------------------------------------------------------------
; put_decimal - writes an unsigned number in decimal.
; @param  rax  the number
; @param  rdi  the destination (advanced past the digits)
; Clobbers rax, rcx, rdx, r9, r10.
; -----------------------------------------------------------------------------
put_decimal:
    mov     r9, rsp                     ; digits go below rsp, last first
    mov     r10d, 10
.divide:
    xor     edx, edx
    div     r10
    add     dl, '0'
    dec     r9
    mov     [r9], dl
    test    rax, rax
    jnz     .divide
.copy:
    mov     al, [r9]
    mov     [rdi], al
    inc     rdi
    inc     r9
    cmp     r9, rsp
    jb      .copy
    ret

; -----------------------------------------------------------------------------
; usage_error - the usage line on standard error, then exit 2.
; -----------------------------------------------------------------------------
usage_error:
    lea     rsi, [msg_usage]
    mov     edx, msg_usage_len
    mov     edi, STDERR
    call    write_all
    mov     edi, 2
    jmp     exit

; -----------------------------------------------------------------------------
; exit - ends the process.
; @param  edi  the exit status
; exit_group rather than exit: exit ends only the calling thread.
; -----------------------------------------------------------------------------
exit:
    mov     eax, SYS_EXIT_GROUP
    syscall

section .note.GNU-stack noalloc noexec nowrite progbits
