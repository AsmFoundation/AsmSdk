# Calling assembly from .NET, Python and C

An AsmSdk shared library is an ordinary Windows DLL, Linux `.so` or macOS `.dylib` that follows
its platform's C calling convention, so any language with a C foreign-function interface can
call it. This page covers the three callers the AsmFoundation uses, plus testing.


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
    /// <summary>int hello_add(int a, int b), exported by hello.dll, libhello.so or libhello.dylib.</summary>
    [LibraryImport("hello", EntryPoint = "hello_add")]
    internal static partial int HelloAdd(int a, int b);
}
```

The .NET project needs nothing else. Every `.asmproj` carries an implicit `PackageReference` to
`AsmFoundation.Sdk`, and the package's `buildTransitive` targets reach the projects that
reference it. They treat the asm project like a NuGet package with a `runtimes/` folder, and
let .NET's own RID rules choose the library.

- **A portable build** (no `RuntimeIdentifier`, the default for `dotnet build`) builds every RID
  the asm project lists. Each library goes to `runtimes/<rid>/native/` in the output, and
  `App.deps.json` lists them. The .NET host then loads the one for the machine the app runs
  on, as it does for a package's native libraries. The same for `dotnet publish`.
- **A RID-specific build** (`dotnet build -r linux-arm64`, or a `RuntimeIdentifier` in the
  `.csproj`) builds the one RID that serves it best, by the .NET SDK's RID graph, and copies
  that library flat, beside `App.dll`. That is the RID itself when the asm project lists it.
  Otherwise it is the nearest one the RID falls back to: an asm project that lists `linux-arm64`
  serves `linux-musl-arm64`, as a package's `runtimes/linux-arm64/` would. When none fits, the
  build stops with **ASM0040**, which lists what the asm project builds and what the app can
  use.
- **`RuntimeIdentifiers` on the reference** narrows what a portable build carries:

  ```xml
  <ProjectReference Include="../Hello/Hello.asmproj" RuntimeIdentifiers="linux-x64;win-x64" />
  ```

  A RID the asm project does not list is ASM0012, as with `-r`. An asm project that lists no
  RIDs of its own builds exactly these. A portable build that names none for such a project
  stops with ASM0010. A RID-specific build needs no list: it builds its own RID.
- **Only shared libraries are copied.** A referenced static library, program or object-file
  project is something an app cannot load. It is skipped with a message, and stays for the
  asm projects that link it. This includes the static libraries that the .NET SDK adds as
  transitive references of an asm reference.
- **The app's own publish options stay with the app.** `SelfContained`, `PublishAot`,
  `PublishTrimmed`, `PublishSingleFile` and `PublishReadyToRun` are not passed on: the asm
  project builds the same native code either way. That holds on the command line too
  (`dotnet publish -p:PublishAot=true`), where NuGet's restore hands them to every project:
  the asm projects ignore them.
- **Through a class library.** An app that reaches the asm project through a C# wrapper
  (app → `Acme.Crc.Net.csproj` → `Crc.asmproj`) gets the same result. The .NET SDK adds the
  asm project to the app's references transitively, and the consumer targets build it for the
  app. The wrapper itself is RID-agnostic, so it is built portable and keeps every RID's
  `runtimes/` copy in its own output. A RID-specific app leaves those out of its output and
  publish folders, as NuGet leaves out other RIDs' assets.
- **`-o`:** `dotnet build App.csproj -o out` builds the asm projects in their own `bin/`
  folders, and `out/` gets the copies the app needs. Footgun: `-o` sends every project's
  output to `out/`, so a wrapper library in between puts its portable `runtimes/` copies there
  as well. `dotnet publish -o` has no such effect.
- **No assembly reference** is added. The SDK's `GetTargetPathWithTargetPlatformMoniker`
  returns nothing.
- **Library names:** `[LibraryImport("hello")]` and `[DllImport("hello")]` probe `hello.dll`
  on Windows, `libhello.so` on Linux and `libhello.dylib` on macOS. Use the bare name, and
  one attribute serves every platform.
- **Platforms:** the app runs wherever the asm project has a build. On another platform, the
  first call throws `DllNotFoundException`.
- **A single solution builds both** with `dotnet build App.slnx`, as long as the
  `.asmproj` entry has its type GUID.

## Where the binaries go: BinariesCopyPath

By default the asm libraries go where NuGet puts a package's native files: `runtimes/<rid>/native/`
in a portable build, beside the app in a RID-specific one. `BinariesCopyPath` puts them
elsewhere, for build and publish alike:

```xml
<!-- One reference -->
<ProjectReference Include="../Hello/Hello.asmproj" BinariesCopyPath="native/{rid}" />

<!-- Every asm reference of the project (and its asm packages) -->
<PropertyGroup>
  <BinariesCopyPath>RidFolders</BinariesCopyPath>
</PropertyGroup>
```

| Value | A portable build (every RID) | A build for one RID (`-r win-x64`) |
|---|---|---|
| `Auto` (the default) | `runtimes/<rid>/native/hello.dll` | `hello.dll`, beside the app |
| `Runtimes` | `runtimes/<rid>/native/hello.dll` | `runtimes/win-x64/native/hello.dll` |
| `AppFolder` | `hello.dll`, `libhello.so`, `libhello.dylib`, beside the app (one RID per OS; see ASM0044 below) | `hello.dll` |
| `RidFolders` | `<rid>/hello.dll` | `win-x64/hello.dll` |
| any folder, `{rid}` optional | `native/{rid}` → `native/<rid>/hello.dll` | `native/{rid}` → `native/win-x64/hello.dll`; `native` → `native/hello.dll` |

- **`{rid}`** is the RID each file was built for. In a build for one RID it is the RID that
  serves it: a `linux-musl-arm64` app that gets the `linux-arm64` build has it in `linux-arm64/`.
- **A folder without `{rid}`** holds a file name once. In a portable build, `win-x64` and
  `win-arm64` both give `hello.dll`, and `linux-x64`, `linux-arm64` and `linux-musl-x64` all
  give `libhello.so`, so such a build stops with **ASM0044** before anything is copied. Add
  `{rid}`, or narrow the reference's `RuntimeIdentifiers` to one RID per OS. The three OSes
  never collide: their file names differ.
- **Values:** a preset (`Auto`, `Runtimes`, `AppFolder`, `RidFolders`, in any letter case), or a
  folder inside the output: slashes either way, no absolute path, no `..`, none of
  `< > : " | ? *`, and no placeholder but `{rid}`. Anything else stops with **ASM0043**, which
  says where the value was written.
- **Which value applies,** per asm library: the reference's own attribute, then the project's
  property, then the choice of a .NET library the asm library comes through, then `Auto`. So
  `BinariesCopyPath` on the reference inside a wrapper library, often the only place the asm
  project is referenced, shapes the app's output as well; the app's own property or reference
  overrides it. When two libraries in between disagree, **ASM0046** warns and the first wins.
- **Footgun:** `RidFolders` in a portable build writes `bin/Debug/net10.0/win-x64/`, the same
  folder `dotnet build -r win-x64` uses for the whole app. Prefer `native/{rid}` when you also
  build for one RID.

### How the app finds them

No code is needed in the app. Measured with .NET 10 on Windows and Linux:

- **`deps.json`:** the app's `deps.json` lists every asm library with `localPath`, the path it
  was copied to: `runtimeTargets` in a portable app, plain `native` assets in an app for one
  RID. The .NET host loads from any folder named there, framework-dependent and
  self-contained alike. A self-contained app ignores `runtimeTargets`, and without `localPath`
  the host keeps only a file name and looks beside the app.
- **The generated loader:** a C# project for .NET 5 or later that puts an asm library
  anywhere but the app folder gets `obj/.../AsmNativeLibraryLoader.g.cs`. It is a module
  initializer that adds an `AssemblyLoadContext.ResolvingUnmanagedDll` handler, for what reads
  no `deps.json`: NativeAOT, and an app that gets the files through a library without the
  entries (`PrivateAssets="all"` below). It runs only after .NET's own probing failed, and
  after any resolver of yours (`NativeLibrary.SetDllImportResolver` runs first). It looks
  for `<name>.dll`, `lib<name>.so` or `lib<name>.dylib` under the app folder, for this
  machine's RID, and on musl for the `linux-<arch>` build too. Turn it off with
  `<AsmGenerateNativeLibraryLoader>false</AsmGenerateNativeLibraryLoader>`; a VB or F#
  project, or one for an older framework, never gets it.

| Publish kind | How the library in `native/` was found |
|---|---|
| portable, `dotnet build` / `dotnet run` | `deps.json` (`runtimeTargets` + `localPath`) |
| `-r <rid>`, framework-dependent | `deps.json` (`native` + `localPath`) |
| `-r <rid> --self-contained` | `deps.json` (`native` + `localPath`) |
| `-p:PublishSingleFile=true` | the library stays in its folder beside the bundle |
| `-p:PublishAot=true` | the generated loader |

### PrivateAssets="all": the binaries inside a .NET package

`dotnet pack` turns a `ProjectReference` into a package dependency, an asm reference too: a
wrapper library `Acme.Calc` that references `Calc.asmproj` depends on the package `calc`, and
both must be published. With NuGet's own `PrivateAssets="all"` ("this reference does not flow
to my users"), the wrapper's package carries every RID's library itself, and depends on
nothing:

```xml
<ProjectReference Include="../Calc.Native/Calc.Native.asmproj" PrivateAssets="all" />
```

```text
Acme.Calc.1.0.0.nupkg
  lib/net10.0/Acme.Calc.dll
  runtimes/win-x64/native/calc.dll
  runtimes/linux-x64/native/libcalc.so
  runtimes/osx-arm64/native/libcalc.dylib     (one per RID of the reference)
  buildTransitive/net10.0/Acme.Calc.targets   BinariesCopyPath for the package's users
```

- **Use it** for a library whose asm code is an implementation detail. Keep the dependency
  when several .NET packages share one asm library: NuGet then ships it once.
- **Within the solution,** an app that references the wrapper by project doesn't get the asm
  reference (it does not flow). Unless the app has asm references of its own, it doesn't get
  AsmSdk's targets either. So the app:
  - copies what the wrapper hands on: the files where the wrapper's `BinariesCopyPath` put
    them, every RID's (nothing in the app keeps only one);
  - gets no `deps.json` entries;
  - loads the library through the loader generated into the wrapper.

  In a single-file publish the files stay beside the bundle. To place them elsewhere, set
  `BinariesCopyPath` on the wrapper's reference.

## Publishing a .NET app

Every publish mode of .NET carries the asm library, the way it carries a NuGet package's native
library (measured with the .NET 10 and 11 SDKs, through a project reference and through a
package):

| `dotnet publish …` | Where the asm library goes |
|---|---|
| (portable) | `runtimes/<rid>/native/`, listed in `deps.json` |
| `-r <rid>` | beside the app |
| `-r <rid> -p:PublishSingleFile=true` | beside the single file |
| the same with `-p:IncludeNativeLibrariesForSelfExtract=true` | inside the single file; the .NET host extracts it at start, so the file runs alone |
| `-r <rid> -p:PublishAot=true` | beside the native program, loaded at run time (NativeAOT does not link a shared library in). NativeAOT needs its platform linker, as always: Visual Studio's C++ tools on Windows, clang on Linux, Xcode on macOS |
| `-r <rid> -p:PublishTrimmed=true` | beside the app; trimming leaves native files alone |
| `-r linux-x64 -t:PublishContainer` | in the image, beside the app |

The single-file rows rely on the consumer targets, which mark the asm libraries
`AssetType=native`: the metadata the .NET SDK decides by. "Beside the app" is the default:
with `BinariesCopyPath` each row puts the library in that folder instead (see above).

**A static library inside a NativeAOT program.** NativeAOT can link a static library into the
program and call it directly: no library file ships, and nothing is looked up at run time. The
consumer targets leave static libraries alone, so name the library yourself, for the RID being
published, and build it for that RID first (`dotnet build Crc.asmproj -c Release -r win-x64`):

```xml
<ItemGroup Condition="'$(RuntimeIdentifier)' != '' and '$(PublishAot)' == 'true'">
  <DirectPInvoke Include="crc" />  <!-- the name in [LibraryImport("crc")] -->
  <NativeLibrary Condition="$(RuntimeIdentifier.StartsWith('win-'))"
                 Include="../Crc/bin/$(Configuration)/freestanding/$(RuntimeIdentifier)/crc.lib" />
  <NativeLibrary Condition="!$(RuntimeIdentifier.StartsWith('win-'))"
                 Include="../Crc/bin/$(Configuration)/freestanding/$(RuntimeIdentifier)/libcrc.a" />
</ItemGroup>
```

- **Declare its entry points with `HIDDEN_FUNCTION`.** A `FUNCTION` in a static library takes
  its export along into whatever links it, so a Windows program would export it.
  `HIDDEN_FUNCTION` links the same and exports nothing (measured on Windows: the program's
  export table then holds only .NET's own entry; a Linux program exports neither).
- **A JIT run cannot use it.** `dotnet run` has no static library to load and throws
  `DllNotFoundException`; only the published program has the code linked in.

## .NET through a package

- **What a `.csproj` gets:** a `.csproj` with `<PackageReference Include="Acme.Crc" />` gets
  the package's `runtimes/<rid>/native/` libraries (`crc.dll`, `libcrc.so`, `libcrc.dylib`).
  NuGet handles them on its own: a portable app gets every RID under `runtimes/`, and the host
  picks one. An app built for one RID gets the best match from NuGet's RID graph, copied
  beside it.
- **What it ignores:** the package's `native/` folder (static and import libraries) and the asm
  items in its `build/` props. These apply only to asm projects.
- **BinariesCopyPath on the PackageReference** (or the project's property) moves the package's
  libraries like a project reference's:

  ```xml
  <PackageReference Include="Acme.Crc" Version="1.2.0" BinariesCopyPath="native/{rid}" />
  ```

  An asm package does not depend on AsmFoundation.Sdk, so the work is done by targets inside
  the package (`buildTransitive/<tfm>/<id>.targets`, plain MSBuild, named for that package
  only): they move NuGet's items, and the .NET SDK itself writes the new place into
  `deps.json` as `localPath`. A C# consumer gets a generated loader for NativeAOT, as above.
  Without the attribute or the property, nothing changes.
- **Wrapper packages:** to ship a managed API, make a separate `.csproj` package (for
  example `Acme.Crc.Net`) that references the native package and contains the
  `[LibraryImport]` declarations. The pair works like `SkiaSharp` with its
  `SkiaSharp.NativeAssets.*` packages. Or put the binaries inside the wrapper's package
  with `PrivateAssets="all"` (above).

**Example:** [Shipping to .NET through NuGet](../examples/nuget-for-dotnet.md) packs an assembly library for seven RIDs and its C# wrapper as two packages, and uses them from a program by version.

## Marshalling rules

- **Blittable types cross for free:**
  - `int` ↔ `int32_t`, `long` ↔ `int64_t`, `nint` ↔ `intptr_t`;
  - `double` ↔ `double`;
  - pointers, and `struct`s made only of these, with `[StructLayout(LayoutKind.Sequential)]`.

  .NET calls with the platform's C convention by default: Microsoft x64 on Windows, System V on
  Linux and macOS, AAPCS64 on ARM64. Those are the conventions `asmsdk.inc` and
  `asmsdk-gnu.inc` follow, so no `CallingConvention` is needed.
- **Spans:** pass `ReadOnlySpan<byte>` as a pointer plus a length. With `[LibraryImport]`,
  declare the parameter as `byte*` and call it inside `fixed`, or use
  `ReadOnlySpan<byte>` directly (the source generator pins it).
- **Strings:** use `StringMarshalling = StringMarshalling.Utf8` for `const char*` arguments.
  The generator allocates a NUL-terminated UTF-8 copy for each call. On hot paths, pass
  bytes instead.
- **`bool`:** marshal as `[MarshalAs(UnmanagedType.U1)]` (1 byte), or use `byte`/`int`
  explicitly. Read only the low byte of a 1-byte bool: `ARG1b` in x64 code (`cl` on Windows,
  `dil` elsewhere), `uxtb w0, w0` on ARM64.
- **Very short leaf functions:** `[SuppressGCTransition]` removes the GC mode switch,
  which costs a few nanoseconds per call and dominates a call that only adds two numbers.
  It is only for functions that:
  - run briefly;
  - never block;
  - never call back into .NET;
  - and never take locks.

  Every asm leaf that only computes qualifies.
- **Callbacks:** an `[UnmanagedCallersOnly]` static method's address (`&Method`) can be
  passed to asm as a function pointer. The asm calls it with the platform's C convention. In
  x64 code, the arguments go in `ARG1`.. and, on Windows, `SHADOW_SPACE` is reserved.
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

- **Every platform tests its own build:** the test project's portable output carries every RID
  of the asm project, as an app's does. The same test run on a Windows, Linux or macOS CI
  machine calls that machine's library. A machine whose platform the asm project does not
  build gets `DllNotFoundException`, so skip the tests there.
- **Compare against a reference:** for every function, write a C# reference
  implementation and compare outputs on random inputs, including the edge cases: zero
  length, unaligned pointers, page boundaries, maximum values.
- **Memory boundaries:** to test that code never reads past the end of a buffer, allocate
  the buffer at the end of a page followed by an inaccessible page: `mmap` plus `mprotect` on
  Linux and macOS, `VirtualAlloc` plus `VirtualProtect` on Windows, through P/Invoke. A read
  past the end then crashes the test instead of passing silently.

**Example:** [Testing and measuring from C#](../examples/testing-and-benchmarks.md) tests each code path of an assembly library against a C# reference with xunit, and times it with BenchmarkDotNet.

## Python

```python
import ctypes

lib = ctypes.CDLL("./bin/Release/freestanding/linux-x64/libhello.so")
# Windows: ctypes.CDLL(r".\bin\Release\freestanding\win-x64\hello.dll")
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
- **Distributing:** ship one wheel per platform, with the library loaded relative to the
  module file. The platform tags:
  - `win_amd64` and `win_arm64`;
  - `manylinux_2_34_x86_64` and `manylinux_2_34_aarch64` for `libc` builds (glibc 2.34+), or
    `manylinux2014_x86_64` and `manylinux2014_aarch64` for `freestanding` ones (no glibc
    requirement);
  - `musllinux_1_2_x86_64` for `linux-musl-x64`;
  - `macosx_11_0_x86_64` and `macosx_11_0_arm64`.
- **`cffi`:** `cffi` in ABI mode reads a C header, so the `include/<name>.h` you ship for C
  users serves Python too.

Both templates in [Writing NASM for AsmSdk](writing-nasm.md) were called this way from Python
3.12, and returned the expected values: on Windows 11, on Debian (`python:3.12-slim`, glibc)
and on Alpine (`python:3.12-alpine`, musl).

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

- **Windows:** link the DLL's import library, and put the DLL beside the program:
  `cl main.c /Iinclude bin\Release\freestanding\win-x64\hello.dll.lib` (checked with MSVC
  2022).
- **Linux:** `gcc main.c -Iinclude -Lbin/Release/freestanding/linux-x64 -lhello`. Run it with
  `LD_LIBRARY_PATH` set, or link with `-Wl,-rpath,'$ORIGIN'`.
- **macOS:** `clang main.c -Iinclude -Lbin/Release/freestanding/osx-arm64 -lhello
  -Wl,-rpath,@executable_path`. The library's install name is `@rpath/libhello.dylib`, so the
  program finds it through its own run paths.
- **A static library** links in directly: `libhello.a` on Linux and macOS, `hello.lib` on
  Windows. For GNU `ld` on Linux, its objects must carry `.note.GNU-stack`, or recent
  versions warn that the stack is executable.

**Rust, Go and Zig** bind the same functions through their C FFI (`extern "C"`, cgo,
`@cImport`).
