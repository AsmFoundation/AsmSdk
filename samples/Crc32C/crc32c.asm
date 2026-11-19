; =============================================================================
; crc32c.asm - libcrc32c: CRC-32C (Castagnoli) as a freestanding shared library
; (Crc32C sample).
;
; Exports (crc32c.map, version CRC32C_1.0; System V AMD64 ABI):
;     uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);
;     int      crc32c_implementation(void);
;     uint32_t crc32c_append_portable(uint32_t crc, const void *data, size_t length);
;     uint32_t crc32c_append_sse42(uint32_t crc, const void *data, size_t length);
;     uint32_t crc32c_append_sse42_3way(uint32_t crc, const void *data, size_t length);
;
; The convention is zlib's crc32(): pass 0 to start a checksum, and a previous
; result to continue it, so crc32c_append(crc32c_append(0, a), b) is the
; checksum of a followed by b. "123456789" gives 0xE3069283.
;
; Three paths compute the same function. The first call to crc32c_append asks
; the CPU (cpuid) which ones it can run, and every later call jumps straight to
; the fastest:
;   portable    a 256-entry table, one byte per step. Any x86-64 CPU.
;   sse42       SSE4.2's crc32 instruction, 8 bytes per step. Each step needs the
;               previous step's result, and crc32 takes 3 cycles, so the
;               instruction idles 2 cycles out of 3: about 8 bytes per 3 cycles.
;   sse42_3way  splits a block into three lanes and runs three independent
;               crc32 chains, so the instruction issues every cycle (about 3x
;               sse42 on large inputs), then merges the three partial checksums
;               with PCLMULQDQ (carry-less multiplication). SSE4.2 + PCLMULQDQ.
; The path functions are exported so the tests can check each one and the
; benchmark can time each one. They check nothing: calling an sse42 path on a
; CPU without the instructions raises SIGILL. Programs call crc32c_append.
;
; Nothing here needs libc, so the library is built freestanding (no DT_NEEDED)
; and loads into any process: a .NET program, a C program, another asm one.
; =============================================================================

default rel
bits 64

%define CRC32C_BUILDING                 ; we define the symbols crc32c.inc declares
%include "crc32c.inc"

; cpuid leaf 1, ECX: the feature bits the dispatcher tests.
%define CPUID_ECX_PCLMULQDQ     1
%define CPUID_ECX_SSE42         20

; -----------------------------------------------------------------------------
; X_POW n - assigns CRC32C_XPOW the polynomial x^n mod P, bit-reflected (bit 31
; holds x^0), computed at assembly time.
; Multiplying a reflected value by x is one step of the bitwise CRC: shift
; right, and fold the polynomial back in when a bit falls off. The table below
; is built with the same step, so the constants and the table agree by
; construction rather than by copying numbers from elsewhere.
; The result goes to a fixed name instead of one passed in: NASM expands a
; macro parameter before %assign reads it, so a name that already holds a value
; would arrive as that value and could not be assigned again.
; @param  %1  the exponent n (n >= 0)
; -----------------------------------------------------------------------------
%macro X_POW 1
    %assign CRC32C_XPOW 0x80000000
    %rep %1
        %assign CRC32C_XPOW (CRC32C_XPOW >> 1) ^ (CRC32C_POLY & -(CRC32C_XPOW & 1))
    %endrep
%endmacro

; -----------------------------------------------------------------------------
; THREE_WAY lane - runs the 3-lane kernel over every whole block of 3 * lane
; bytes that is left, then falls through with the rest.
;
; Merging three lanes. Let c0, c1 and c2 be the raw (not inverted) CRC
; registers after the three lanes of one block, lane 0 continuing the checksum
; and lanes 1 and 2 starting from 0. The block's CRC is
;     shift(c0, 2 * lane) xor shift(c1, lane) xor c2
; where shift(c, n) runs c through n zero bytes, which multiplies it by x^(8n)
; mod P. One carry-less multiply and one crc32 do that: crc32 over 8 bytes
; multiplies by x^32, and a carry-less product of two reflected values carries
; one extra factor x, so
;     crc32_u64(0, clmul(c, K)) = shift(c, n)   for K = x^(8n - 33) mod P.
; The kernel saves lane 2's last 8 bytes for the merge and folds both products
; into them, so one crc32 finishes the block:
;     crc = crc32_u64(c2, last8 xor clmul(c0, K_2lane) xor clmul(c1, K_lane))
; The two K are computed at assembly time from lane, so a different lane size
; needs no other change. (The derivation was checked on a bit-exact model of
; crc32 for every basis register and 10,000 random ones; the tests compare
; every path with a table-driven reference.)
;
; Why it is faster: the three crc32 per iteration do not wait for each other,
; so the CPU issues one per cycle instead of one per 3 cycles. Lane 0's chain
; also waits for the previous block's merge, so a small lane pays that latency
; more often: the function runs 1,536-byte blocks first, then 192-byte ones.
;
; @param  %1      lane: bytes per lane, a multiple of 8, at least 16
; in/out  eax     the raw CRC register (upper half of rax zero)
; in/out  rsi     the data; advanced past the blocks processed
; in/out  rdx     bytes left; reduced by the blocks processed
; Clobbers rcx, r8, r9, r10, xmm0, xmm1, xmm2.
; -----------------------------------------------------------------------------
%macro THREE_WAY 1
    %if (%1 % 8) != 0 || %1 < 16
        %error "THREE_WAY: the lane must be a multiple of 8, at least 16"
    %endif
    ; Plain names, reassigned by each use of the macro: the values are emitted
    ; into .rodata right below, before the next use can change them.
    X_POW 8 * 2 * %1 - 33
    %assign CRC32C_K_TWO_LANES CRC32C_XPOW
    X_POW 8 * %1 - 33
    %assign CRC32C_K_ONE_LANE CRC32C_XPOW

[section .rodata]
    align 16
    ; PCLMULQDQ picks one quadword of each operand: 0x00 takes the low one
    ; (c0's multiplier), 0x10 the high one (c1's).
%%k:    dq CRC32C_K_TWO_LANES, CRC32C_K_ONE_LANE
__SECT__

    cmp     rdx, 3 * %1
    jb      %%done
    movdqa  xmm0, [rel %%k]

%%block:
    xor     r8d, r8d                ; lane 1 starts from 0
    xor     r9d, r9d                ; lane 2 starts from 0
    mov     ecx, %1 / 8 - 1         ; all but the last 8 bytes of each lane

%%lanes:
    crc32   rax, qword [rsi]                ; lane 0
    crc32   r8, qword [rsi + %1]            ; lane 1
    crc32   r9, qword [rsi + 2 * %1]        ; lane 2
    add     rsi, 8
    dec     ecx
    jnz     %%lanes

    ; rsi = block + lane - 8: the last 8 bytes of lane 0.
    crc32   rax, qword [rsi]                ; lane 0 complete: c0
    crc32   r8, qword [rsi + %1]            ; lane 1 complete: c1
    movq    xmm1, rax
    movq    xmm2, r8
    pclmulqdq xmm1, xmm0, 0x00              ; c0 * x^(8 * 2 lane - 33)
    pclmulqdq xmm2, xmm0, 0x10              ; c1 * x^(8 * lane - 33)
    pxor    xmm1, xmm2
    movq    r10, xmm1
    xor     r10, qword [rsi + 2 * %1]       ; folded into lane 2's last 8 bytes
    crc32   r9, r10                         ; lane 2 complete: the block's CRC
    mov     eax, r9d

    add     rsi, 2 * %1 + 8                 ; to the next block
    sub     rdx, 3 * %1
    cmp     rdx, 3 * %1
    jae     %%block
%%done:
%endmacro

section .text

; -----------------------------------------------------------------------------
; crc32c_append - the CRC-32C of data, continued from crc, on the fastest path
; this CPU supports.
;
; One indirect jump through `dispatch`. Before the first call, dispatch points
; at `resolve`, which picks the path, stores it and jumps there; afterwards it
; points at the path itself, so the choice costs nothing per call.
; @param  edi  crc: 0 for a new checksum, or a previous result to continue
; @param  rsi  data (may be anything when rdx is 0)
; @param  rdx  length in bytes
; @returns eax = the checksum
; -----------------------------------------------------------------------------
global crc32c_append:function
crc32c_append:
    jmp     [rel dispatch]

; -----------------------------------------------------------------------------
; resolve - the first call's target: picks the path, then continues as that
; path with the caller's arguments untouched.
; Entered by a jump, so the stack holds the caller's return address and the
; path returns straight to the caller.
; -----------------------------------------------------------------------------
resolve:
    call    select
    jmp     rax

; -----------------------------------------------------------------------------
; crc32c_implementation - which path crc32c_append runs on this CPU.
; Picks it first if no call has yet, so the answer never changes afterwards.
; @returns eax = CRC32C_PORTABLE, CRC32C_SSE42 or CRC32C_SSE42_3WAY
; -----------------------------------------------------------------------------
global crc32c_implementation:function
crc32c_implementation:
    mov     rax, [rel dispatch]
    lea     rcx, [rel resolve]
    cmp     rax, rcx
    jne     .known
    call    select
.known:
    mov     edx, CRC32C_PORTABLE
    lea     rcx, [rel impl_sse42]
    cmp     rax, rcx
    jne     .not_sse42
    mov     edx, CRC32C_SSE42
.not_sse42:
    lea     rcx, [rel impl_sse42_3way]
    cmp     rax, rcx
    jne     .done
    mov     edx, CRC32C_SSE42_3WAY
.done:
    mov     eax, edx
    ret

; -----------------------------------------------------------------------------
; select - picks the fastest path this CPU supports and stores it in dispatch.
;
; Thread safety without a lock: threads that race through here all store the
; same value, and an aligned 8-byte store is atomic on x86-64, so every reader
; sees either `resolve` (and selects again, harmlessly) or a complete path.
; The paths are named by local labels (impl_*), not by their exported names: a
; shared library may not take the address of its own exported symbols
; PC-relatively, because another library could interpose them.
; @returns rax = the chosen path's address
; Preserves rdi, rsi, rdx (crc32c_append's arguments) and rbx (callee-saved),
; which cpuid would otherwise destroy. Clobbers rcx, r8, r9.
; -----------------------------------------------------------------------------
select:
    mov     r8, rbx
    mov     r9, rdx
    mov     eax, 1
    cpuid                                   ; ecx = feature flags
    mov     rbx, r8
    mov     rdx, r9

    lea     rax, [rel impl_portable]
    bt      ecx, CPUID_ECX_SSE42
    jnc     .store
    lea     rax, [rel impl_sse42]
    bt      ecx, CPUID_ECX_PCLMULQDQ
    jnc     .store
    lea     rax, [rel impl_sse42_3way]
.store:
    mov     [rel dispatch], rax
    ret

; -----------------------------------------------------------------------------
; crc32c_append_portable - the table path: one byte per step, any x86-64.
; @param  edi  crc to continue (0 for a new checksum)
; @param  rsi  data
; @param  rdx  length in bytes
; @returns eax = the checksum
; -----------------------------------------------------------------------------
global crc32c_append_portable:function
crc32c_append_portable:
impl_portable:
    mov     eax, edi
    not     eax                             ; the register runs inverted
    test    rdx, rdx
    jz      .done
    lea     r8, [rel crc32c_table]
.byte:
    movzx   ecx, byte [rsi]
    xor     cl, al                          ; index = (register ^ byte) & 0xFF
    shr     eax, 8
    xor     eax, [r8 + rcx * 4]
    inc     rsi
    dec     rdx
    jnz     .byte
.done:
    not     eax
    ret

; -----------------------------------------------------------------------------
; crc32c_append_sse42 - the crc32 instruction, 8 bytes per step, one chain.
; Needs SSE4.2 (not checked here).
; @param  edi  crc to continue (0 for a new checksum)
; @param  rsi  data
; @param  rdx  length in bytes
; @returns eax = the checksum
; -----------------------------------------------------------------------------
global crc32c_append_sse42:function
crc32c_append_sse42:
impl_sse42:
    mov     eax, edi
    not     eax

; sse42_rest - the single-chain tail shared with the 3-way path.
; in: eax = raw register, rsi = data, rdx = length. Returns the checksum.
sse42_rest:
    mov     rcx, rdx
    shr     rcx, 3                          ; whole quadwords
    jz      .bytes
.qwords:
    crc32   rax, qword [rsi]
    add     rsi, 8
    dec     rcx
    jnz     .qwords
.bytes:
    and     edx, 7                          ; 0-7 bytes left
    jz      .done
.byte:
    crc32   eax, byte [rsi]
    inc     rsi
    dec     edx
    jnz     .byte
.done:
    not     eax
    ret

; -----------------------------------------------------------------------------
; crc32c_append_sse42_3way - three crc32 chains per block, merged with
; PCLMULQDQ; inputs shorter than one block take the single chain.
; Needs SSE4.2 and PCLMULQDQ (not checked here).
; @param  edi  crc to continue (0 for a new checksum)
; @param  rsi  data
; @param  rdx  length in bytes
; @returns eax = the checksum
; -----------------------------------------------------------------------------
global crc32c_append_sse42_3way:function
crc32c_append_sse42_3way:
impl_sse42_3way:
    mov     eax, edi
    not     eax
    THREE_WAY 512                           ; 1,536-byte blocks
    THREE_WAY 64                            ; then 192-byte blocks
    jmp     sse42_rest                      ; then 8 bytes, then 1 byte at a time

section .rodata
align 64
; crc32c_table[b] = the CRC register after one byte b from 0: the byte-wise
; step of the portable path. Built at assembly time with the same reflected
; multiply-by-x step as X_POW.
crc32c_table:
%assign CRC32C_BYTE 0
%rep 256
    %assign CRC32C_REG CRC32C_BYTE
    %rep 8
        %assign CRC32C_REG (CRC32C_REG >> 1) ^ (CRC32C_POLY & -(CRC32C_REG & 1))
    %endrep
    dd CRC32C_REG
    %assign CRC32C_BYTE CRC32C_BYTE + 1
%endrep

section .data
align 8
; Where crc32c_append jumps: `resolve` until the first call has picked a path.
; A pointer in a writable section, so the loader fills it in with a relative
; relocation; in .rodata the link would fail (it would need a text relocation).
dispatch:   dq resolve

section .note.GNU-stack noalloc noexec nowrite progbits
