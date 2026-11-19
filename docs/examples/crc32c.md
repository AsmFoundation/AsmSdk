# Crc32C: an assembly library for .NET

[`samples/Crc32C`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C) computes
CRC-32C, the Castagnoli checksum that iSCSI, ext4, Btrfs, SCTP and gRPC use, in about 340 lines
of NASM (half of them comments), and [`samples/Crc32C.Net`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Net)
lets C# call it. It is built the way you would ship a hot routine in native code:

- **a freestanding shared library,** `libcrc32c.so`, about 5.7 KB with no `DT_NEEDED` at all,
  so it loads into any Linux x64 process;
- **three code paths and a dispatcher** that picks the fastest one the CPU supports, once;
- **a kernel that keeps the `crc32` instruction busy every cycle:** 21.8 GB/s on 1 MiB, 3.7
  times the C# loop with `BitOperations.Crc32C` and 2.75 times the obvious loop in assembly
  ([measured](testing-and-benchmarks.md#results));
- **a stable binary interface:** five exported names, versioned, and nothing else visible.

| File | What it holds |
|---|---|
| `Crc32C.asmproj` | the project: `AssemblyName`, `ExportsFile`, a `LinkerArg`, the package metadata |
| `crc32c.asm` | the dispatcher, the three paths, the table and constants |
| `crc32c.map` | the exports file (a linker version script) |
| `include/crc32c.inc` | the interface for NASM callers, packed with the library |
| `../Crc32C.Net/Crc32C.cs` | the C# API: `Crc32C.HashToUInt32`, `Crc32C.Append`, `Crc32C.Path` |

## The API

```c
uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);

/* The path crc32c_append runs here: 1 portable, 2 sse42, 3 sse42 + 3-way */
int crc32c_implementation(void);
```

The convention is zlib's (and `System.IO.Hashing`'s): pass 0 to start a checksum, and a previous
result to continue one, so appending `b` to the checksum of `a` gives the checksum of `a`
followed by `b`. `"123456789"` gives `0xE3069283`, the catalogue check value.

The three paths are exported too, as `crc32c_append_portable`, `crc32c_append_sse42` and
`crc32c_append_sse42_3way`, so that the tests can check each one and the benchmark can time each
one. They check nothing: calling an SSE4.2 path on a CPU without the instruction raises `SIGILL`.

## The project

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>crc32c</AssemblyName>
    <ExportsFile>crc32c.map</ExportsFile>
    <PackageId>Samples.Crc32C</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <LinkerArg Include="-z defs" />
  </ItemGroup>
</Project>
```

- **No `TargetFramework`:** the default, `freestanding`. Nothing here calls libc.
- **`-z defs`** fails the link if any symbol is left undefined, instead of failing when the
  library is loaded. For a freestanding library that proves it really needs nothing.
- **`PackageId` and `Version`** make `dotnet pack` produce `Samples.Crc32C.1.0.0.nupkg`; see
  [Shipping to .NET through NuGet](nuget-for-dotnet.md).

## The exports file

```text
CRC32C_1.0 {
  global:
    crc32c_append;
    crc32c_implementation;
    crc32c_append_portable;
    crc32c_append_sse42;
    crc32c_append_sse42_3way;
  local:
    *;
};
```

`ExportsFile` is NativeAOT's name for the linker's version script. Only the five names under
`global` stay in the dynamic symbol table, each as `name@@CRC32C_1.0`; `local: *` hides every
other label (the dispatcher, the table), so no other library can call or replace them. A later
release would add names under a node `CRC32C_1.1` that inherits `CRC32C_1.0`, and programs built
against 1.0 keep loading.

## One dispatcher, three paths

```nasm
crc32c_append:
    jmp     [rel dispatch]          ; dispatch starts as `resolve`

resolve:
    call    select                  ; picks the path, stores it in dispatch
    jmp     rax                     ; and continues as that path

select:
    mov     r8, rbx                 ; cpuid destroys rbx (callee-saved) and rdx (an argument)
    mov     r9, rdx
    mov     eax, 1
    cpuid
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
```

- **The choice costs nothing after the first call:** `crc32c_append` is a single indirect jump
  through `dispatch`, which then points at the path itself.
- **No lock is needed.** Threads that race through `select` all store the same value, and an
  aligned 8-byte store is atomic on x86-64, so a reader sees either `resolve` or a whole path.
- **`dispatch` lives in `.data`.** It holds an address, so the loader patches it with one
  relative relocation; in `.rodata` the link would fail, because that would need a text
  relocation. (See [Writing NASM for AsmSdk](../guide/writing-nasm.md#position-independent-code).)
- **The paths are named by local labels** (`impl_sse42`), not by their exported names: a shared
  library may not take the address of its own exported symbol PC-relatively, because another
  library could interpose it.

| Path | How | Needs |
|---|---|---|
| portable | a 256-entry table, one byte per step | any x86-64 |
| sse42 | the `crc32` instruction, 8 bytes per step | SSE4.2 |
| sse42_3way | three `crc32` chains per block, merged with PCLMULQDQ | SSE4.2 and PCLMULQDQ |

## Constants computed by NASM

The table and the kernel's merge constants are not copied from anywhere: NASM's preprocessor
computes them from the polynomial while it assembles the file.

```nasm
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
```

The same reflected "multiply by x" step, repeated n times from x⁰, gives x^n mod P, which is
what the kernel needs (below). A macro, `X_POW n`, runs it, so changing the kernel's lane size
recomputes its constants with no other edit.

## The 3-way kernel

`crc32` takes 3 cycles to produce a result but can start a new one every cycle. The obvious
loop, 8 bytes per instruction, makes each step wait for the previous one, so the instruction
idles two cycles out of three. The kernel splits each block into three lanes and runs three
independent chains, so a new `crc32` starts every cycle:

```nasm
%%lanes:
    crc32   rax, qword [rsi]                ; lane 0
    crc32   r8, qword [rsi + %1]            ; lane 1
    crc32   r9, qword [rsi + 2 * %1]        ; lane 2
    add     rsi, 8
    dec     ecx
    jnz     %%lanes
```

The three partial checksums are then merged. Lane 0's must be moved past the 2 lanes that follow
it, and lane 1's past 1 lane: "moving a CRC past n zero bytes" multiplies it by x^(8n) mod P. One
carry-less multiply (PCLMULQDQ) and one `crc32` do that, with the constant x^(8n−33) mod P
(`crc32` over 8 bytes multiplies by x³², and the reflected product carries one extra x). Both
products are folded into lane 2's last 8 bytes, so a single `crc32` finishes the block:

```nasm
    movq    xmm1, rax                       ; c0
    movq    xmm2, r8                        ; c1
    pclmulqdq xmm1, xmm0, 0x00              ; c0 * x^(8 * 2 lane - 33)
    pclmulqdq xmm2, xmm0, 0x10              ; c1 * x^(8 * lane - 33)
    pxor    xmm1, xmm2
    movq    r10, xmm1
    xor     r10, qword [rsi + 2 * %1]       ; lane 2's last 8 bytes
    crc32   r9, r10                         ; the block's CRC
```

The function runs 1,536-byte blocks (3 × 512), then 192-byte blocks (3 × 64), then the single
chain for the rest. The first lane of each block waits for the previous block's merge, so small
lanes pay that latency more often; two sizes keep both large and medium inputs fast.

The derivation was checked on a bit-exact model of `crc32` for every basis state and 10,000
random ones before the code was written; the tests then compare every path with a C# reference
at every length from 0 to 5,000 bytes, and a build with a wrong exponent fails 6 of them (see
[Testing and measuring from C#](testing-and-benchmarks.md)).

## Calling it from C#

`Crc32C.Net` is a class library with a `ProjectReference` to the `.asmproj`; the build copies
`libcrc32c.so` next to its assembly.

```csharp
[assembly: DisableRuntimeMarshalling]

public static unsafe partial class Crc32C
{
    public static uint Append(uint crc, ReadOnlySpan<byte> source)
    {
        fixed (byte* data = source)
        {
            return source.Length <= NoTransitionLimit
                ? AppendShort(crc, data, (nuint)source.Length)
                : AppendLong(crc, data, (nuint)source.Length);
        }
    }

    [LibraryImport("crc32c", EntryPoint = "crc32c_append")]
    [SuppressGCTransition]
    private static partial uint AppendShort(uint crc, byte* data, nuint length);

    [LibraryImport("crc32c", EntryPoint = "crc32c_append")]
    private static partial uint AppendLong(uint crc, byte* data, nuint length);
}
```

- **`[LibraryImport]`** generates the call at compile time, and with only `uint`, `byte*` and
  `nuint` in the signature there is nothing to marshal; `DisableRuntimeMarshalling` makes that a
  compile-time rule.
- **`SuppressGCTransition` for inputs up to 4 KiB.** A normal call switches the thread to
  preemptive mode and back: an empty call measured 9.6 ns with the switch and 3.4 ns without,
  which matters for a 64-byte record. Without the switch the GC cannot suspend the thread during
  the call, so it is kept for short inputs only: even the single-chain path finishes 4 KiB in
  well under a microsecond.
- **`Crc32C.Append(crc, data, path)`** runs one path, for tests and benchmarks, after checking
  with `Sse42.X64.IsSupported` and `Pclmulqdq.IsSupported` that the CPU has it: the native path
  functions check nothing, and `SIGILL` would end the process.
- **The library loads at the first call.** On anything but Linux x64 that call throws
  `DllNotFoundException`.

## How it is checked

- **[Its own tests](testing-and-benchmarks.md):** 21 xunit tests run every path against a C#
  reference.
- **[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs):**
  the exact exported names and their version, no `DT_NEEDED` and no text relocation, the
  constants NASM computed (`0x170076FA`, `0xDD7E3B0C`, `0x0D3B6092`, `0x9E4ADDF8` and the
  table's first entries), the packages, and the library's tests and benchmark check on Linux.
