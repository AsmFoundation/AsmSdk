; =============================================================================
; crc32c.inc - libcrc32c's interface for NASM callers (Crc32C sample).
;
; A project that references Crc32C (ProjectReference, or the Samples.Crc32C
; package) gets this folder on its include path. From an executable, call the
; library through the PLT:
;
;     %include "crc32c.inc"
;     xor     edi, edi                ; crc = 0: a new checksum
;     lea     rsi, [rel data]
;     mov     edx, data_len
;     call    crc32c_append wrt ..plt ; eax = CRC-32C of data
;
; Every function follows the System V AMD64 ABI. The checksum convention is
; zlib's: pass 0 to start, and the previous result to continue, so appending b
; to the checksum of a gives the checksum of a followed by b.
; =============================================================================

%ifndef CRC32C_INC
%define CRC32C_INC

; The CRC-32C (Castagnoli) polynomial 0x1EDC6F41, bit-reflected: the form the
; SSE4.2 crc32 instruction and every byte-wise table use. iSCSI, SCTP, ext4,
; Btrfs, RocksDB and gRPC's checksums use this polynomial.
%define CRC32C_POLY             0x82F63B78

; What crc32c_implementation() returns: the path crc32c_append runs on this CPU.
%define CRC32C_PORTABLE         1       ; table, one byte per step
%define CRC32C_SSE42            2       ; crc32 instruction, one chain
%define CRC32C_SSE42_3WAY       3       ; three crc32 chains + PCLMULQDQ merge

; The library's own source defines these symbols, and NASM refuses a name that
; is both extern and defined in one file, so the library sets CRC32C_BUILDING.
%ifndef CRC32C_BUILDING

; uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);
; The checksum of data, continued from crc (0 for a new checksum), on the
; fastest path this CPU supports. Safe from any thread.
extern crc32c_append

; int crc32c_implementation(void);
; Which path crc32c_append runs here: CRC32C_PORTABLE, CRC32C_SSE42 or
; CRC32C_SSE42_3WAY.
extern crc32c_implementation

; The three paths, for tests and benchmarks. They check nothing: the sse42
; ones raise SIGILL on a CPU without SSE4.2 (and PCLMULQDQ for _3way).
extern crc32c_append_portable
extern crc32c_append_sse42
extern crc32c_append_sse42_3way

%endif ; CRC32C_BUILDING
%endif ; CRC32C_INC
