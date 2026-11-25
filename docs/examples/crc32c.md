# Crc32C: an assembly library for .NET, on seven platforms

[`samples/Crc32C`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C) computes
CRC-32C, the Castagnoli checksum that iSCSI, ext4, Btrfs, SCTP and gRPC use. It is about 380
lines of NASM for x64 and 300 lines of GNU-syntax assembly for ARM64, half of them comments.
[`samples/Crc32C.Net`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Net)
lets C# call it. It is built the way you would ship a hot routine in native code:

- **a freestanding shared library for all seven RIDs.** It needs nothing to load, so it loads
  into any process of its platform:
  - `crc32c.dll`, 4.5 KB, with no imports on x64 and one on ARM64 (kernel32's
    `IsProcessorFeaturePresent`);
  - `libcrc32c.so`, 5.6 KB, with no `DT_NEEDED`, for glibc and musl alike;
  - `libcrc32c.dylib`, with no dependent library.
- **three code paths and a dispatcher** that picks the fastest one the CPU supports, once;
- **a kernel that keeps the CRC instruction busy every cycle:** 21.8 GB/s on 1 MiB on x64,
  3.7 times the C# loop with `BitOperations.Crc32C` and 2.75 times the obvious loop in assembly
  ([measured](testing-and-benchmarks.md#results));
- **a stable binary interface:** five exported names on every OS (versioned on Linux), and
  nothing else visible.

| File | What it holds |
|---|---|
| `Crc32C.asmproj` | the project: the seven RIDs, `AssemblyName`, `ExportsFile`, a Linux `LinkerArg`, the package metadata |
| `crc32c.asm` | the x64 code (NASM, through `asmsdk.inc`): the dispatcher, the three paths, the table and constants |
| `crc32c.s` | the ARM64 code (GNU syntax, through `asmsdk-gnu.inc`): the same exports and the same algorithm |
| `crc32c.map` | the exports file (a linker version script) |
| `include/crc32c.inc` | the interface for NASM callers, packed with the library |
| `../Crc32C.Net/Crc32C.cs` | the C# API: `Crc32C.HashToUInt32`, `Crc32C.Append`, `Crc32C.Path` |

## The API

```c
uint32_t crc32c_append(uint32_t crc, const void *data, size_t length);

/* The path crc32c_append runs here: 1 portable, 2 hardware, 3 hardware + 3-way */
int crc32c_implementation(void);
```

The convention is zlib's (and `System.IO.Hashing`'s): pass 0 to start a checksum, and a previous
result to continue one, so appending `b` to the checksum of `a` gives the checksum of `a`
followed by `b`. `"123456789"` gives `0xE3069283`, the catalogue check value.

The three paths are exported too, as `crc32c_append_portable`, `crc32c_append_hardware` and
`crc32c_append_hardware_3way`, so that the tests can check each one and the benchmark can time
each one. They check nothing: calling a hardware path on a CPU without the instructions raises
`SIGILL` (an illegal-instruction exception on Windows).

## The project

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
    <AssemblyName>crc32c</AssemblyName>
    <ExportsFile>crc32c.map</ExportsFile>
    <PackageId>Samples.Crc32C</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <LinkerArg Include="-z defs" Condition="$(RuntimeIdentifier.StartsWith('linux'))" />
  </ItemGroup>
</Project>
```

- **Seven RIDs from two sources.** `crc32c.asm` has no qualifier, so it builds every x64 RID.
  `crc32c.s` builds every ARM64 RID. See
  [Sources per platform](../guide/platforms.md#sources-per-platform).
- **No `TargetFramework`:** the default, `freestanding`. Nothing here calls a C library.
- **`-z defs`** fails a Linux link if any symbol is left undefined, instead of failing when the
  library is loaded. For a freestanding library that proves it really needs nothing. It is
  ld.lld's syntax, so the item is conditioned on the Linux RIDs. lld-link and ld64.lld refuse
  undefined symbols anyway.
- **`PackageId` and `Version`** make `dotnet pack` produce `Samples.Crc32C.1.0.0.nupkg`, with
  every RID's library in it; see [Shipping to .NET through NuGet](nuget-for-dotnet.md).

## The exports file

```text
CRC32C_1.0 {
  global:
    crc32c_append;
    crc32c_implementation;
    crc32c_append_portable;
    crc32c_append_hardware;
    crc32c_append_hardware_3way;
  local:
    *;
};
```

`ExportsFile` is NativeAOT's name for the linker's version script. On Linux, only the five names
under `global` stay in the dynamic symbol table, each as `name@@CRC32C_1.0`; `local: *` hides
every other label (the dispatcher, the table), so no other library can call or replace them. A
later release would add names under a node `CRC32C_1.1` that inherits `CRC32C_1.0`, and programs
built against 1.0 keep loading. On Windows and macOS, the SDK hands the same five names to
lld-link (`-export:`) and ld64.lld (`-exported_symbol`), which have no versions.

## One library, two calling conventions

The x64 file serves Windows, whose convention passes the arguments in `rcx`, `rdx`, `r8`, and
Linux and macOS, whose System V convention uses `rdi`, `rsi`, `rdx`. Each exported function is
an `asmsdk.inc` `FUNCTION` that moves its OS's `ARG1`..`ARG3` into a private convention every
path shares:

```nasm
FUNCTION crc32c_append, 3
    mov     eax, ARG1d                  ; the crc
    mov     r10, ARG2                   ; the data
    mov     r11, ARG3                   ; the length
    jmp     [rel dispatch]              ; dispatch starts as `resolve`
ENDFUNCTION
```

`eax`, `r10` and `r11` are volatile (caller-saved) in both conventions, as is every other
register the paths touch (`rcx`, `rdx`, `r8`, `r9`, `xmm0`-`xmm2`). So no path saves a register
on any OS, and the kernels keep the shape they had when the library was Linux-only. ARM64 needs
no such step: Windows, Linux and macOS all pass the arguments in `w0`, `x1`, `x2`.

## One dispatcher, three paths

```nasm
resolve:
    call    select                  ; picks the path, stores it in dispatch
    mov     rcx, rax
    mov     eax, r9d                ; select handed the crc back in r9d: rax holds the path
    jmp     rcx                     ; and continues as that path

select:
    mov     r8, rbx                 ; cpuid destroys rbx, callee-saved on every OS
    mov     r9d, eax
    mov     eax, 1
    cpuid
    mov     rbx, r8
    lea     rax, [rel impl_portable]
    bt      ecx, CPUID_ECX_SSE42
    jnc     .store
    lea     rax, [rel impl_hardware]
    bt      ecx, CPUID_ECX_PCLMULQDQ
    jnc     .store
    lea     rax, [rel impl_hardware_3way]
.store:
    mov     [rel dispatch], rax
    ret
```

- **The choice costs nothing after the first call:** `crc32c_append` is a single indirect jump
  through `dispatch`, which then points at the path itself.
- **No lock is needed.** Threads that race through `select` all store the same value, and an
  aligned 8-byte store is atomic on x86-64 (and single-copy atomic on ARM64), so a reader sees
  either `resolve` or a whole path.
- **`dispatch` lives in `.data`.** It holds an address, so the loader fixes it up when the
  library loads, which needs a writable section. In `.rodata` the Linux link would fail, and a
  macOS build would stop with ASM0081 (see
  [Writing NASM for AsmSdk](../guide/writing-nasm.md#position-independent-code)).
- **The paths are named by local labels** (`impl_hardware`), not by their exported names, the
  habit of ELF shared libraries, where another library could interpose an exported symbol.

On ARM64, the same dispatcher asks each OS in its own way:

| OS | How `crc32c.s` picks the path |
|---|---|
| Linux | reads the `ID_AA64ISAR0_EL1` register (its CRC32 and AES fields). Linux emulates the read for user code (since kernel 4.11) |
| Windows | calls kernel32's `IsProcessorFeaturePresent` for the CRC32 and the crypto instructions: the library's one import. The SDK links kernel32 into every Windows output |
| macOS | takes the 3-way path: every Apple silicon CPU has both extensions, and macOS does not let user code read the ID registers |

| Path | x64 | ARM64 |
|---|---|---|
| portable | a 256-entry table, one byte per step; any x86-64 | the same table; any ARM64 |
| hardware | SSE4.2's `crc32`, 8 bytes per step | the CRC32 extension's `crc32cx`, 8 bytes per step |
| hardware_3way | three `crc32` chains per block, merged with PCLMULQDQ | three `crc32cx` chains, merged with PMULL (the AES extension's carry-less multiply) |

## Constants computed by the assemblers

The table and the kernel's merge constants are not copied from anywhere: each assembler computes
them from the polynomial while it assembles the file. NASM does it with its preprocessor:

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
recomputes its constants with no other edit. `crc32c.s` does the same with llvm-mc's `.rept` and
`.set`, and gets the same numbers: `crc32cx` is the same function as x64's `crc32 r64`, and PMULL
the same product as PCLMULQDQ.

## The 3-way kernel

`crc32` takes 3 cycles to produce a result but can start a new one every cycle. The obvious
loop, 8 bytes per instruction, makes each step wait for the previous one, so the instruction
idles two cycles out of three. The kernel splits each block into three lanes and runs three
independent chains, so a new `crc32` starts every cycle:

```nasm
%%lanes:
    crc32   rax, qword [r10]                ; lane 0
    crc32   r8, qword [r10 + %1]            ; lane 1
    crc32   r9, qword [r10 + 2 * %1]        ; lane 2
    add     r10, 8
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
    movq    rdx, xmm1
    xor     rdx, qword [r10 + 2 * %1]       ; lane 2's last 8 bytes
    crc32   r9, rdx                         ; the block's CRC
```

The ARM64 kernel is the same, instruction for instruction: `crc32cx` for `crc32`, `pmull` for
`pclmulqdq`. The function runs 1,536-byte blocks (3 × 512), then 192-byte blocks (3 × 64), then
the single chain for the rest. The first lane of each block waits for the previous block's
merge, so small lanes pay that latency more often; two sizes keep both large and medium inputs
fast.

The derivation was checked on a bit-exact model of `crc32` for every basis state and 10,000
random ones before the code was written; the tests then compare every path with a C# reference
at every length from 0 to 5,000 bytes, and a build with a wrong exponent fails 6 of them (see
[Testing and measuring from C#](testing-and-benchmarks.md)).

## Calling it from C#

`Crc32C.Net` is a class library with a `ProjectReference` to the `.asmproj`. The build carries
each RID's library under `runtimes/<rid>/native/`, listed in `deps.json`, and the .NET host
loads the one for the machine it runs on.

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
  compile-time rule. `"crc32c"` finds `crc32c.dll`, `libcrc32c.so` or `libcrc32c.dylib`.
- **`SuppressGCTransition` for inputs up to 4 KiB.** A normal call switches the thread to
  preemptive mode and back: an empty call measured 9.6 ns with the switch and 3.4 ns without,
  which matters for a 64-byte record. Without the switch the GC cannot suspend the thread during
  the call, so it is kept for short inputs only: even the single-chain path finishes 4 KiB in
  well under a microsecond.
- **`Crc32C.Append(crc, data, path)`** runs one path, for tests and benchmarks, after checking
  that the CPU has it: `Sse42.X64` and `Pclmulqdq` on x64, `Crc32.Arm64` and `Aes` on ARM64. The
  native path functions check nothing, and an illegal instruction would end the process.
- **The library loads at the first call.** On a platform none of the seven builds serves
  (32-bit x86, for one), that call throws `DllNotFoundException`.

## In WebAssembly

> [!NOTE]
> **Not released yet:** WebAssembly is on the main branch only (see
> [WebAssembly](../guide/webassembly.md)).

The project lists two more RIDs, `browser-wasm` and `wasi-wasm`, and a third source,
`crc32c.wasm.s`, that builds them: the same five exports, the same checksums, as a WebAssembly
module (`crc32c.wasm`) and the archive a .NET WebAssembly app links (`crc32c.a`). `dotnet pack`
puts both under `native/<rid>/freestanding/` in the package, next to the seven native libraries.

WebAssembly has no CRC instruction and no carry-less multiply, not in SIMD128 and not in
relaxed SIMD, so there is one real path, a table one. It is the fast table method, not the x64
fallback's byte at a time: **slicing-by-8**, eight 256-entry tables that advance the checksum 8
bytes per step. Each step is eight independent lookups, which the engine can overlap; one table
would make a chain of eight dependent ones.

```asm
        ADDRESS tables          // the tables' address (asmsdk-wasm.inc)
        local.get 3             // the first 4 bytes, xor the checksum
        i32.const 255
        i32.and
        i32.const 2
        i32.shl
        i32.add
        i32.load 7168           // T7[one & 255]: the load's offset immediate picks the table
```

- **The tables are computed at the first call.** NASM builds the x64 table while it assembles;
  WebAssembly has no assembler constants at all (see
  [the footguns](../guide/webassembly.md#footguns)), so `crc32c_init` fills the 8 KB once, into
  zero-filled memory, before the first checksum: 2,048 values, about 10 µs in Node 22 (V8).
- **`crc32c_implementation` returns 1,** portable. `crc32c_append` and `crc32c_append_portable`
  run the same function; the two hardware exports trap (`unreachable`), as a hardware path
  faults on an x64 CPU without the instructions. `Crc32C.IsSupported` is false for both there,
  so `Crc32C.Net` never calls them.
- **The polynomial is a plain `i32.const 0x82F63B78`.** WebAssembly's constants are signed;
  llvm-mc wraps a value above `0x7FFFFFFF` to the same 32 bits (`-2097792136` gives identical
  bytes).

[`samples/Crc32C.Browser`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Browser)
is a .NET WebAssembly app with one `ProjectReference`, to `Crc32C.Net`. The consumer targets build
`Crc32C.asmproj` for `browser-wasm` and link its archive into the app's runtime, so the
unchanged `[LibraryImport("crc32c")]` calls reach `crc32c.wasm.s`. It runs in Node (V8, Chrome's
engine) or a browser:

```text
$ dotnet run -c Release -- verify
Crc32C.Browser: browser-wasm, .NET 10.0.15, interpreter, libcrc32c path Portable
verified: 123456789 = 0xE3069283; asm, .NET and C# agree on lengths 0-4096 at alignments 0-7, and on a chained append
```

| Mode | What it does |
|---|---|
| `verify` | checks the library against .NET's own CRC-32C (`BitOperations.Crc32C`, a software path on WebAssembly) and against the same slicing-by-8 written in C# |
| `bench` | times all three in the same engine, in paired rounds |
| `hash <file>` | what the page shows, for one file: each implementation's checksum and throughput |
| the page | `index.html` in the bundle: choose or drop a file, and the browser checksums it all three ways |

The page and `hash` start the runtime without `Main` and call one `[JSExport]` method,
`Page.Hash`, with the file's bytes. To open the page, serve the bundle folder over HTTP (a browser
loads no module or WebAssembly from `file://`), for example:

```sh
python -m http.server 8000 --directory bin/Release/net10.0/browser-wasm/AppBundle
```

`bench` measures both of .NET's ways to run the C#: Mono's interpreter, a .NET WebAssembly app's
default, and ahead-of-time compilation (`dotnet publish -c Release -p:RunAOTCompilation=true`:
.NET compiles a WebAssembly app ahead of time only when it publishes it, and a build with the
property set still interprets). The app needs the `wasm-tools` workload, so it is not in
`Samples.slnx`.

**Measured** on one performance core of an i9-13900K, in Node 22.14 and Chrome 156, three runs
each ([results](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Crc32C.Browser/results/2026-11-24-i9-13900K)),
at 1 MiB:

| C# runs | asm | .NET `BitOperations.Crc32C` | C# slicing-by-8 |
|---|---:|---:|---:|
| interpreted, Node | **1237 MB/s** | 186 MB/s (asm 6.66x) | 239 MB/s (asm 5.18x) |
| interpreted, Chrome | **1247 MB/s** | 73 MB/s (asm 17.1x) | 157 MB/s (asm 7.97x) |
| AOT, Node | 1236 MB/s | 280 MB/s (asm 4.42x) | **1267 MB/s** (asm 0.98x) |
| AOT, Chrome | **1249 MB/s** | 290 MB/s (asm 4.32x) | 1181 MB/s (asm 1.06x) |

- **Against .NET's CRC-32C, the method wins:** .NET has no CRC instruction on WebAssembly and
  reads one table once per byte, where slicing-by-8 makes eight independent reads per 8 bytes.
- **Against the same method in C#, AOT is a tie:** from 4 KiB up, the asm is 0.98 to 1.07 times
  the AOT-compiled C#. Hand-writing it pays only against the interpreter, .NET's default, where
  the asm is 4.9 to 8.0 times faster.
- **At 64 bytes the call weighs as much as the work** (the asm reaches a fifth of its large-input
  speed when interpreted C# calls it); see the results for every size.

> [!TIP]
> **Time a Release build or a publish, never a Debug build.** .NET writes `debugLevel: -1` into a
> browser app's *build* bundle, and in Chromium and Firefox the runtime then starts with its
> debugger on: the interpreter keeps every method unoptimized, and the jiterpreter (which
> compiles hot interpreted loops to WebAssembly) compiles nothing. Measured in headless Chrome:
> the C# slicing-by-8 ran at 5 to 7 MB/s, with 0 traces compiled, against 134 MB/s with
> `debugLevel: 0`. Node never turns the debugger on, so it hides the difference. The SDK sets
> `WasmDebugLevel` to 0 in the Release build of every browser app that links asm code, and -1 in
> Debug ([WebAssembly](../guide/webassembly.md#in-a-net-webassembly-app)); `Crc32C.Browser` also
> sets it itself, as it did when these numbers were taken. A publish says 0 on its own.

## How it is checked

- **[Its own tests](testing-and-benchmarks.md):** 21 xunit tests run every path against a C#
  reference. CI runs them natively on the five build machines (Windows x64 and Arm, Linux x64
  and ARM64, Apple silicon), each with its own platform's build. In Docker they also pass on
  Alpine (musl), and under ARM64 emulation the library returns the same checksums.
- **[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs):**
  the exact exported names and their version, no `DT_NEEDED` and no text relocation, the
  constants NASM computed (`0x170076FA`, `0xDD7E3B0C`, `0x0D3B6092`, `0x9E4ADDF8` and the
  table's first entries), the packages with their seven RIDs, and the library's tests and
  benchmark check on the machine running them. On main, the package also carries the two wasm
  RIDs' module and archive, and `Crc32C.Browser` runs `verify` in Node wherever the
  `wasm-tools` workload is (CI: Linux x64 and Windows x64).
