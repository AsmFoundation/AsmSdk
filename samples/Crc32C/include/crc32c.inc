; =============================================================================
; crc32c.inc - libcrc32c's interface for NASM callers (Crc32C sample).
;
; A project that references Crc32C (ProjectReference, or the Samples.Crc32C
; package) gets this folder on its include path. From x64 code, with the SDK's
; asmsdk.inc for the platform's argument registers:
;
;     %include "asmsdk.inc"
;     %include "crc32c.inc"
;     xor     ARG1d, ARG1d            ; crc = 0: a new checksum
;     lea     ARG2, [rel data]
;     mov     ARG3d, data_len
;     call    crc32c_append PLT       ; eax = CRC-32C of data
;
; Every function follows the platform's C calling convention (Microsoft x64 on
; Windows, System V elsewhere; AAPCS64 on ARM64, where crc32c.s implements the
; same functions). The checksum convention is zlib's: pass 0 to start, and the
; previous result to continue, so appending b to the checksum of a gives the
; checksum of a followed by b.
; =============================================================================

%ifndef CRC32C_INC
%define CRC32C_INC

; The CRC-32C (Castagnoli) polynomial 0x1EDC6F41, bit-reflected: the form the
; SSE4.2 crc32 and ARMv8 crc32c instructions and every byte-wise table use. iSCSI, SCTP, ext4,
; Btrfs, RocksDB and gRPC's checksums use this polynomial.
%define CRC32C_POLY             0x82F63B78

; What crc32c_implementation() returns: the path crc32c_append runs on this CPU.
%define CRC32C_PORTABLE         1       ; table, one byte per step
%define CRC32C_HARDWARE         2       ; crc32 instruction, one chain (x64: SSE4.2;
                                        ; ARM64: crc32cx)
%define CRC32C_HARDWARE_3WAY    3       ; three crc32 chains + carry-less merge (x64:
                                        ; PCLMULQDQ; ARM64: PMULL)

; The library's own source defines these symbols, and NASM refuses a name that
; is both extern and defined in one file, so the library sets CRC32C_BUILDING.
%ifndef CRC32C_BUILDING

; uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);
; The checksum of data, continued from crc (0 for a new checksum), on the
; fastest path this CPU supports. Safe from any thread.
extern crc32c_append

; int crc32c_implementation(void);
; Which path crc32c_append runs here: CRC32C_PORTABLE, CRC32C_HARDWARE or
; CRC32C_HARDWARE_3WAY.
extern crc32c_implementation

; The three paths, for tests and benchmarks. They check nothing: the hardware
; ones fault on a CPU without the CRC instructions (and the carry-less multiply
; for _3way).
extern crc32c_append_portable
extern crc32c_append_hardware
extern crc32c_append_hardware_3way

%endif ; CRC32C_BUILDING
%endif ; CRC32C_INC
