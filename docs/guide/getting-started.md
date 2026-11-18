# Getting started

This page builds three things:

1. a shared library written in NASM;
2. a freestanding executable;
3. a C# program that calls the library.

All three go in one solution, built with `dotnet build`.

## What you need

- **The .NET SDK.** AsmSdk's own CI uses .NET 10. `.slnx` solutions need the .NET 9.0.200
  SDK or later.
- **A Windows, Linux or macOS build machine:**
  - **Windows and Linux:** x64.
  - **macOS:** Apple silicon.
  - **Anything else:** see [supported build machines](../toolchain/index.md#supported-build-machines).
- **Linux x86-64 to run the outputs.** A Linux machine, WSL, or a container is enough.
  The build itself runs anywhere on the list above.

You don't install an assembler or a linker: restore downloads them.

## 1. Name the SDK

Create `global.json` next to where your solution will be:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.1.0" } }
```

MSBuild resolves `<Project Sdk="AsmFoundation.Sdk">` through this entry, from nuget.org.

## 2. A shared library

`Hello/Hello.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <AsmLibraryName>hello</AsmLibraryName>   <!-- bin/Debug/libhello.so -->
  </PropertyGroup>
</Project>
```

`Hello/hello.asm`:

```nasm
default rel
section .text

global hello_add:function
hello_add:                  ; int hello_add(int a, int b)
    lea     eax, [rdi + rsi]
    ret

section .note.GNU-stack noalloc noexec nowrite progbits
```

Build it:

```sh
dotnet build Hello/Hello.asmproj
```

The first build restores `AsmFoundation.Sdk` and the toolchain package for your machine.
It then writes `Hello/bin/Debug/libhello.so`.

- **Every `*.asm` in the project folder is a source,** the way every `*.cs` is compiled
  in a `.csproj`.
- **A second `dotnet build` with no changes runs no tool at all.**

> [!TIP]
> `default rel` and `global …:function` matter more than they look. They are two of the
> rules in [Writing NASM for AsmSdk](writing-nasm.md) that decide whether code links into
> a shared library.

## 3. An executable

`HelloApp/HelloApp.asmproj`:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AsmLibraryName>hello-app</AsmLibraryName>
  </PropertyGroup>
</Project>
```

`HelloApp/main.asm`: a freestanding program that writes a line with the `write` system
call, then exits.

```nasm
default rel

%define SYS_WRITE       1
%define SYS_EXIT_GROUP  231

section .rodata
message:        db "hello from assembly", 10
message_len     equ $ - message

section .text
global _start:function
_start:                     ; the kernel enters here; there is no return address
    mov     eax, SYS_WRITE
    mov     edi, 1          ; stdout
    lea     rsi, [message]
    mov     edx, message_len
    syscall
    mov     eax, SYS_EXIT_GROUP
    xor     edi, edi        ; status 0
    syscall

section .note.GNU-stack noalloc noexec nowrite progbits
```

`dotnet build HelloApp/HelloApp.asmproj` writes `HelloApp/bin/Debug/hello-app`, a static
Linux executable.

**Running it on Linux:**

```sh
./HelloApp/bin/Debug/hello-app
```

**Running it from Windows,** in a container. Files built on Windows have no execute bit,
so set it first:

```sh
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/runtime:10.0 \
  sh -c 'chmod +x HelloApp/bin/Debug/hello-app && ./HelloApp/bin/Debug/hello-app'
```

To use glibc instead (`printf`, `malloc`, threads), set `AsmRuntime` to `Libc` and write
`main`. See [Runtimes and linking](runtimes-and-linking.md).

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
    [LibraryImport("hello", EntryPoint = "hello_add")]   // probes libhello.so on Linux
    internal static partial int HelloAdd(int a, int b);
}
```

**What the reference does:**

- it builds `Hello` first;
- it copies `libhello.so` next to `App.dll`, on build and on `dotnet publish`;
- it adds no assembly reference.

**Running it** needs Linux x64:

```sh
dotnet build App/App.csproj
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/runtime:10.0 dotnet App/bin/Debug/net10.0/App.dll
```

It prints `42`.

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

- [Projects](projects.md): output kinds, include folders, defines and configurations.
- [References and packages](references-and-packages.md): share a library between
  projects, or as a NuGet package by version.
- [The Claude Code plugin](../ecosystem/claude-code.md): `new_asmproj.py` scaffolds
  any of the projects above, and `check_asmproj.py` lints them.
