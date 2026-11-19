# Calling assembly from .NET, Python and C

An AsmSdk shared library is an ordinary Linux x86-64 `.so` that follows the System V ABI,
so any language with a C foreign-function interface can call it. This page covers the
three callers the AsmFoundation uses, plus testing.


## .NET in the same solution

```xml
<!-- App/App.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>   <!-- needed by [LibraryImport] source generation -->
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Hello/Hello.asmproj" />
  </ItemGroup>
</Project>
```

```csharp
using System.Runtime.InteropServices;

internal static partial class Native
{
    /// <summary>int hello_add(int a, int b), exported by libhello.so.</summary>
    [LibraryImport("hello", EntryPoint = "hello_add")]
    internal static partial int HelloAdd(int a, int b);
}
```

- **The reference:**
  - builds the `.asmproj` first, in the same configuration;
  - copies `lib<name>.so` next to the program, on `dotnet build` and on `dotnet publish`;
  - adds no assembly reference: the SDK's `GetTargetPathWithTargetPlatformMoniker` returns
    nothing.
- **Library names:** on Linux, `[LibraryImport("hello")]` and `[DllImport("hello")]` probe
  `libhello.so` in the application folder first. Use the name without `lib` and `.so`, so
  the same attribute keeps working if you also ship builds for other platforms.
- **Platform:** the app runs on Linux x64 only, since the `.so` is an ELF file. On Windows,
  `dotnet run` fails with `DllNotFoundException`. Run it with WSL, in a container
  (`mcr.microsoft.com/dotnet/sdk:10.0`), or in Linux CI.
- **A single solution builds both** with `dotnet build App.slnx`, as long as the
  `.asmproj` entry has its type GUID.

## .NET through a package

- **What a `.csproj` gets:** a `.csproj` with `<PackageReference Include="Acme.Crc" />`
  gets `runtimes/linux-x64/native/libcrc.so`. NuGet copies it for an app built for
  `linux-x64` (or a portable app, which puts it under `runtimes/` and lets the host probe
  it).
- **What it ignores:** the asm items in the package's `build/` props apply only to asm
  projects.
- **Wrapper packages:** to ship a managed API, make a separate `.csproj` package (for
  example `Acme.Crc.Net`) that references the native package and contains the
  `[LibraryImport]` declarations. The pair works like `SkiaSharp` with
  `SkiaSharp.NativeAssets.Linux`.

**Example:** [Shipping to .NET through NuGet](../examples/nuget-for-dotnet.md) packs an assembly library and its C# wrapper as two packages, and uses them from a program by version.

## Marshalling rules

- **Blittable types cross for free:**
  - `int` ↔ `int32_t`, `long` ↔ `int64_t`, `nint` ↔ `intptr_t`;
  - `double` ↔ `double`;
  - pointers, and `struct`s made only of these, with `[StructLayout(LayoutKind.Sequential)]`.

  .NET on Linux x64 uses the System V convention by default, so no `CallingConvention` is
  needed.
- **Spans:** pass `ReadOnlySpan<byte>` as a pointer plus a length. With `[LibraryImport]`,
  declare the parameter as `byte*` and call it inside `fixed`, or use
  `ReadOnlySpan<byte>` directly (the source generator pins it).
- **Strings:** use `StringMarshalling = StringMarshalling.Utf8` for `const char*` arguments.
  The generator allocates a NUL-terminated UTF-8 copy for each call. On hot paths, pass
  bytes instead.
- **`bool`:** marshal as `[MarshalAs(UnmanagedType.U1)]` (1 byte), or use `byte`/`int`
  explicitly. Write the asm to read `dil`, not `edi`, for a 1-byte bool.
- **Very short leaf functions:** `[SuppressGCTransition]` removes the GC mode switch,
  which costs a few nanoseconds per call and dominates a call that only adds two numbers.
  It is only for functions that:
  - run briefly;
  - never block;
  - never call back into .NET;
  - and never take locks.

  Every asm leaf that only computes qualifies.
- **Callbacks:** an `[UnmanagedCallersOnly]` static method's address (`&Method`) can be
  passed to asm as a function pointer. The asm calls it with the System V convention.
- **Errors:** return status codes, or `-errno` style values. An exception cannot cross the
  boundary.

## Testing asm code from .NET

A test project is the most productive harness for asm functions: property-based checks,
comparison against a C# reference implementation, and fuzzing.

```xml
<!-- Hello.Tests/Hello.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="..." />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="..." />
    <ProjectReference Include="../Hello/Hello.asmproj" />
  </ItemGroup>
</Project>
```

- **Platform guard:** tests that call the `.so` must run on Linux x64. Guard them with
  `OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64`,
  or run the whole suite only in the Linux CI job.
- **Compare against a reference:** for every function, write a C# reference
  implementation and compare outputs on random inputs, including the edge cases: zero
  length, unaligned pointers, page boundaries, maximum values.
- **Memory boundaries:** to test that code never reads past the end of a buffer, allocate
  the buffer at the end of a page followed by a `PROT_NONE` page (`mmap` plus `mprotect`
  via P/Invoke to libc). A read past the end then crashes the test instead of passing
  silently.

**Example:** [Testing and measuring from C#](../examples/testing-and-benchmarks.md) tests each code path of an assembly library against a C# reference with xunit, and times it with BenchmarkDotNet.

## Python

```python
import ctypes

lib = ctypes.CDLL("./bin/Release/freestanding/libhello.so")
lib.hello_add.argtypes = [ctypes.c_int, ctypes.c_int]
lib.hello_add.restype = ctypes.c_int
assert lib.hello_add(40, 2) == 42
```

- **Always set `argtypes` and `restype`:** the default `restype` is `int`, which truncates
  a 64-bit return.
- **Buffers:** pass arrays as `(ctypes.c_int32 * n)(...)`, `ctypes.create_string_buffer`,
  or `numpy_array.ctypes.data_as(ctypes.POINTER(ctypes.c_int32))`.
- **The GIL:** `ctypes.CDLL` releases the GIL around foreign calls, while
  `ctypes.PyDLL` keeps it. For a function that runs only nanoseconds, the `ctypes` call
  overhead dominates, so batch the work into one call per array.
- **Distributing:** ship the `.so` inside a wheel tagged `manylinux_2_34_x86_64` (`libc`)
  or `manylinux2014_x86_64` (`freestanding`: no glibc requirement), and load it relative to
  the module file.
- **`cffi`:** `cffi` in ABI mode reads a C header, so the `include/<name>.h` you ship for C
  users serves Python too.

Both templates in [Writing NASM for AsmSdk](writing-nasm.md) were called this way from Python 3.12 on Ubuntu
24.04 (glibc 2.39) and returned the expected values.

## C and other languages

**Write a header next to the `.inc` file** (`include/hello.h`):

```c
#ifndef HELLO_H
#define HELLO_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
int32_t hello_add(int32_t a, int32_t b);
#ifdef __cplusplus
}
#endif
#endif
```

**Linking from C:**

- `gcc main.c -Iinclude -Lbin/Release/libc -lhello`, then run with `LD_LIBRARY_PATH` set,
  or link with `-Wl,-rpath,'$ORIGIN'`.
- Or link the static library: `gcc main.c bin/Release/libc/libhello.a`. Objects in the `.a` must
  then carry `.note.GNU-stack`, or recent GNU `ld` warns that the stack is executable.

**Rust, Go and Zig** bind the same functions through their C FFI (`extern "C"`, cgo,
`@cImport`).
