# Getting started

This page builds three things:

1. a shared library written in assembly, for seven platforms;
2. a program that prints through the C library, for the same seven;
3. a C# program that calls the library, and runs on any of them.

All three go in one solution, built with `dotnet build`.

## What you need

- **The .NET SDK.** AsmSdk's own CI uses .NET 10. `.slnx` solutions need the .NET 9.0.200
  SDK or later.
- **A build machine with a bundled toolchain:** Windows (x64 or Arm64), Linux (x64 or ARM64,
  glibc) or an Apple silicon Mac. For anything else, see
  [supported build machines](../toolchain/index.md#supported-build-machines).

You don't install an assembler or a linker: restore downloads them. Every build machine builds
for all seven platforms. What it can *run* is its own platform's build, plus the x64 builds
under emulation on Windows on Arm and on Apple silicon (Rosetta 2).

## 1. Name the SDK

Create `global.json` next to where your solution will be:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.3.0" } }
```

MSBuild resolves `<Project Sdk="AsmFoundation.Sdk">` through this entry, from nuget.org.

## 2. A shared library

`Hello/Hello.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AssemblyName>hello</AssemblyName>
    <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

`Hello/hello.asm`, the x64 code:

```nasm
default rel
%include "asmsdk.inc"

section .text
FUNCTION hello_add, 2           ; int hello_add(int a, int b)
    lea     eax, [ARG1 + ARG2]  ; rcx + rdx on Windows, rdi + rsi elsewhere
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```

`Hello/hello.s`, the ARM64 code:

```asm
.include "asmsdk-gnu.inc"

FUNCTION hello_add              // int hello_add(int a, int b)
    add     w0, w0, w1          // the same registers on Windows, Linux and macOS
    ret
ENDFUNCTION hello_add

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
```

Build it:

```sh
dotnet build Hello/Hello.asmproj
```

The first build restores `AsmFoundation.Sdk` and the toolchain package for your machine.
It then builds each platform into its own folder:

```text
Hello/bin/Debug/freestanding/win-x64/hello.dll
Hello/bin/Debug/freestanding/win-arm64/hello.dll
Hello/bin/Debug/freestanding/linux-x64/libhello.so
Hello/bin/Debug/freestanding/linux-arm64/libhello.so
Hello/bin/Debug/freestanding/linux-musl-x64/libhello.so
Hello/bin/Debug/freestanding/osx-x64/libhello.dylib
Hello/bin/Debug/freestanding/osx-arm64/libhello.dylib
```

A Windows DLL also gets its import library, `hello.dll.lib`, for native code that links it, and in
Debug its `hello.pdb`.

- **The properties are .NET's own.** `AssemblyName` names the output, as it names a
  `.csproj`'s DLL; each OS adds its own prefix and extension. `RuntimeIdentifiers` lists the
  platforms, by .NET's runtime identifiers. A project lists the ones its code supports. A
  project that lists none is built for what its referencing projects ask for, or fails with
  ASM0010, which names the ways to choose.
- **The target framework is `freestanding`** when the project sets none: the code links no
  C library. It is the `freestanding` folder in the output path, as `net10.0` is in a
  `.csproj`'s.
- **The file name picks the platforms.** Every `*.asm` and `*.s` in the project folder is a
  source, the way every `*.cs` is compiled in a `.csproj`. `.asm` files are NASM, and build
  the x64 platforms. `.s` files are GNU syntax, assembled by `llvm-mc`, and build the ARM64
  ones. A qualifier narrows a file further (`hello.linux.asm`, `hello.win-arm64.s`): see
  [Sources per platform](platforms.md#sources-per-platform).
- **`asmsdk.inc` and `asmsdk-gnu.inc` are the platform layer.** `FUNCTION` declares and
  exports a function the way each OS does. In x64 code, `ARG1` and `ARG2` are the first two
  argument registers of the OS being built. ARM64 needs no such names: all three OSes pass
  arguments in `x0`-`x7`.
- **A second `dotnet build` with no changes runs no tool at all.**

> [!TIP]
> `default rel` and the `%ifdef LINUX` around `.note.GNU-stack` matter more than they look.
> They are two of the rules in [Writing NASM for AsmSdk](writing-nasm.md): the first decides
> whether code links into a shared library, and the second whether it assembles for macOS.

## 3. A program

`HelloApp/HelloApp.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>libc</TargetFramework>
    <AssemblyName>hello-app</AssemblyName>
    <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
```

`HelloApp/main.asm`: an ordinary C `main` that prints a line with `puts`.

```nasm
default rel
%include "asmsdk.inc"
extern puts

section .rodata
message:    db "hello from assembly", 0

section .text
FUNCTION main                       ; int main(void)
    sub     rsp, SHADOW_SPACE + 8   ; rsp 16-aligned for the call, plus Windows' shadow space
    lea     ARG1, [message]
    call    puts PLT
    add     rsp, SHADOW_SPACE + 8
    xor     eax, eax                ; exit status 0
    RETURN
ENDFUNCTION

%ifdef LINUX
section .note.GNU-stack noalloc noexec nowrite progbits
%endif
```

`HelloApp/main.s`: the same program for ARM64.

```asm
.include "asmsdk-gnu.inc"

RODATA
message:    .asciz "hello from assembly"

FUNCTION main                       // int main(void)
    stp     x29, x30, [sp, #-16]!   // save the frame pointer and the return address
    mov     x29, sp
    ADDRESS x0, message
    CALL    puts
    mov     w0, #0                  // exit status 0
    ldp     x29, x30, [sp], #16
    ret
ENDFUNCTION main

.ifdef LINUX
    .section .note.GNU-stack,"",%progbits
.endif
```

`dotnet build HelloApp/HelloApp.asmproj` writes `HelloApp/bin/Debug/libc/<rid>/hello-app`
(`hello-app.exe` on Windows). Each one is linked against its platform's C library: the
Universal C Runtime on Windows, glibc or musl on Linux, libSystem on macOS. Run the one for
your machine:

```sh
./HelloApp/bin/Debug/libc/win-x64/hello-app.exe      # Windows x64
./HelloApp/bin/Debug/libc/osx-arm64/hello-app        # Apple silicon
./HelloApp/bin/Debug/libc/linux-x64/hello-app        # Linux x64
```

**From Windows, the Linux build runs in a container.** Files built on Windows have no execute
bit, so set it first:

```sh
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/runtime:10.0 \
  sh -c 'chmod +x HelloApp/bin/Debug/libc/linux-x64/hello-app && ./HelloApp/bin/Debug/libc/linux-x64/hello-app'
```

- **`SHADOW_SPACE`** is 32 on Windows and 0 elsewhere. An x64 call needs `rsp` 16-byte aligned,
  and on Windows the caller also reserves 32 bytes above the return address for the callee.
  `puts PLT` calls through the PLT on Linux, and is a plain call elsewhere.
- **`ADDRESS` and `CALL`** spell an address and a call the way the OS's object format wants
  them. Mach-O, for one, names C functions with a leading underscore.
- **A `freestanding` program** links no C library and talks to the OS itself: system calls on
  Linux, kernel32 on Windows. macOS supports no programs without libSystem. See
  [Runtimes and linking](runtimes-and-linking.md) and
  [the C library per platform](platforms.md#the-c-library-per-platform).

## 4. Call it from C#

`App/App.csproj`: a normal console app with a `ProjectReference` to the assembly project.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>   <!-- [LibraryImport] generates unsafe code -->
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Hello/Hello.asmproj" />
  </ItemGroup>
</Project>
```

`App/Program.cs`:

```csharp
using System.Runtime.InteropServices;

Console.WriteLine(Native.HelloAdd(40, 2));

internal static partial class Native
{
    // "hello": hello.dll on Windows, libhello.so on Linux, libhello.dylib on macOS
    [LibraryImport("hello", EntryPoint = "hello_add")]
    internal static partial int HelloAdd(int a, int b);
}
```

**What the reference does:**

- it builds `Hello` first, for each of its platforms;
- it copies each platform's library into `runtimes/<rid>/native/` beside `App.dll`, and lists
  them in `App.deps.json`, on build and on `dotnet publish`. The .NET host loads the one for
  the machine it runs on, as it does for a NuGet package's native libraries;
- it adds no assembly reference.

A build for one platform (`dotnet publish -r linux-arm64`, or a `RuntimeIdentifier` in the
`.csproj`) gets that platform's library alone, copied beside `App.dll`, and NuGet's RID rules
pick it. See [Calling assembly from .NET](interop.md#net-in-the-same-solution).

**Running it** works on any of the seven platforms:

```sh
dotnet run --project App
```

It prints `42`. From Windows, the Linux build runs in a container as well:

```sh
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/runtime:10.0 dotnet App/bin/Debug/net10.0/App.dll
```

## 5. One solution

`App.slnx`:

```xml
<Solution>
  <Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />
  <Project Path="HelloApp/HelloApp.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />
  <Project Path="App/App.csproj" />
</Solution>
```

- **The `Type` GUID is required.** It is the generic MSBuild project type. Without it,
  `dotnet build App.slnx` fails with `MSB4025 … ProjectType '' not found`.
- **Add the entries by hand.** `dotnet sln add` refuses `.asmproj` files ("unknown project
  type").

`dotnet build App.slnx` now builds everything.

## Next steps

- [Platforms and runtime identifiers](platforms.md): which file builds which platform, the
  calling-convention macros, exports, and the C library of each OS.
- [Examples](../examples/index.md): complete programs built the same way: a CRC-32C library
  with hardware paths for x64 and ARM64, called from C# and shipped through NuGet; a `wc` on
  glibc; an `xxd` without libc.
- [Projects](projects.md): output kinds, include folders, defines and configurations.
- [References and packages](references-and-packages.md): share a library between
  projects, or as a NuGet package by version.
- [The Claude Code plugin](../ecosystem/claude-code.md): `new_asmproj.py` scaffolds
  any of the projects above, and `check_asmproj.py` lints them.
