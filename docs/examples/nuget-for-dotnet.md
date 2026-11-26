# Shipping to .NET through NuGet

[Crc32C](crc32c.md) is an assembly library with a C# wrapper. This page ships it the way .NET
libraries with native code ship (SkiaSharp and Microsoft.Data.SqlClient do the same): as two
packages, a native one and a managed one that depends on it. A .NET program then references
the managed package by version and never mentions assembly.

```text
Crc32C.App (net10.0)
  └─ PackageReference Samples.Crc32C.Net 1.0.0        lib/net10.0/Samples.Crc32C.Net.dll
       └─ dependency Samples.Crc32C 1.0.0             runtimes/<rid>/native/: eight RIDs
```

## The native package

`dotnet pack samples/Crc32C/Crc32C.asmproj -c Release` packs the `.asmproj`:

```text
Samples.Crc32C.1.0.0.nupkg
  runtimes/win-x64/native/crc32c.dll                    the library of each RID, where .NET looks for it
  runtimes/win-arm64/native/crc32c.dll
  runtimes/linux-x64/native/libcrc32c.so
  runtimes/linux-arm64/native/libcrc32c.so
  runtimes/linux-musl-x64/native/libcrc32c.so
  runtimes/linux-musl-arm64/native/libcrc32c.so
  runtimes/osx-x64/native/libcrc32c.dylib
  runtimes/osx-arm64/native/libcrc32c.dylib
  native/win-x64/freestanding/crc32c.dll.lib            the DLLs' import libraries, for asm projects
  native/win-arm64/freestanding/crc32c.dll.lib
  native/browser-wasm/freestanding/crc32c.a             the archive a .NET WebAssembly app links
  native/browser-wasm/freestanding/crc32c.wasm          the module, for JavaScript and WASI hosts
  native/wasi-wasm/freestanding/crc32c.a
  native/wasi-wasm/freestanding/crc32c.wasm
  include/crc32c.inc                                    for asm projects that reference the package
  include/crc32c.h                                      the C header, from the PROTO lines
  lib/netstandard1.0/_._                                says which frameworks may use it
  build/netstandard1.0/Samples.Crc32C.props             for asm consumers: include path, link input per RID
  buildTransitive/netstandard1.0/Samples.Crc32C.props
  build/netstandard1.0/Samples.Crc32C.targets           for .NET consumers: BinariesCopyPath, the bindings
  buildTransitive/netstandard1.0/Samples.Crc32C.targets
  contentFiles/cs/net7.0/AsmBindings/crc32c.cs.pp       the C# bindings' baseline, from the PROTO lines
  build/bindings/crc32c.api.json                        the API file and the bindings generator, for [AsmLibrary]
  build/bindings/AsmFoundation.Sdk.Bindings.dll
```

- **`runtimes/<rid>/native/`** is where NuGet puts RID-specific native files. A portable .NET
  program that receives the package gets every RID's file copied into
  `bin/…/runtimes/<rid>/native/`, listed in its `deps.json`, and the .NET host loads the one
  for the machine it runs on. A program built for one RID gets that RID's file, or the nearest
  one by NuGet's RID graph, beside it.
- **The same package serves asm projects:** the include file and the props make it a normal
  [package reference](../guide/references-and-packages.md#consuming-packages) for an `.asmproj`.
  The props pick the build of the referencing project's RID (a Windows DLL is linked through
  its import library), and stop with `ASM0060` for a RID the package lacks.
- **No dependency on the SDK.** Every `.asmproj` carries an implicit `PackageReference` to
  `AsmFoundation.Sdk`, which brings the .NET consumer targets to the projects that reference
  it. Packing removes it from the `.nuspec`: a package's consumers need nothing from the SDK.
- **`netstandard1.0`** is the NuGet framework behind `freestanding`, the default target framework.
  Every .NET project can use it; a `libc` library would be `netstandard2.0`. See
  [Choosing a target framework](../guide/runtimes-and-linking.md#choosing-a-target-framework).

The project sets only what a package needs:

```xml
<PropertyGroup>
  <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;linux-musl-arm64;osx-x64;osx-arm64;browser-wasm;wasi-wasm</RuntimeIdentifiers>
  <AssemblyName>crc32c</AssemblyName>
  <ExportsFile>crc32c.map</ExportsFile>
  <PackageId>Samples.Crc32C</PackageId>
  <Version>1.0.0</Version>
  <Authors>AsmFoundation samples</Authors>
  <Description>Sample: CRC-32C in assembly (x64: SSE4.2 + PCLMULQDQ; ARM64: CRC32 + PMULL), a freestanding shared library for Windows, Linux and macOS.</Description>
</PropertyGroup>
```

`dotnet pack` builds and packs every RID in one run, from any build machine.

## The managed package

`samples/Crc32C.Net` is an ordinary class library with a `ProjectReference` to the `.asmproj`:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <AssemblyName>Samples.Crc32C.Net</AssemblyName>
  <PackageId>Samples.Crc32C.Net</PackageId>
  <Version>1.0.0</Version>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="../Crc32C/Crc32C.asmproj" />
</ItemGroup>
```

`dotnet pack samples/Crc32C.Net/Crc32C.Net.csproj -c Release` turns that reference into a
package dependency, as it does for any referenced project, using the `.asmproj`'s `PackageId`
and `Version`:

```text
Samples.Crc32C.Net.1.0.0.nupkg
  lib/net10.0/Samples.Crc32C.Net.dll
  lib/net10.0/Samples.Crc32C.Net.xml
  Samples.Crc32C.Net.nuspec:
    <dependency id="Samples.Crc32C" version="1.0.0" exclude="Build,Analyzers" />
```

- **The library is not copied into the managed package.** It travels in its own package, so the
  two can version separately and an asm project can use the native one alone.
- **`exclude="Build,Analyzers"`** is NuGet's default for a project dependency: a .NET consumer
  does not need the asm props. The native file is a runtime asset, so it still flows.
- **Pack the native package too.** Packing a project does not pack its references; publish both.

## The consumer

[`samples/consumers/Crc32C.App`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/consumers/Crc32C.App)
is what a user of the package writes:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>crc32c-app</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Samples.Crc32C.Net" Version="1.0.0" />
  </ItemGroup>
</Project>
```

```csharp
uint check = Crc32C.HashToUInt32(Encoding.ASCII.GetBytes("123456789"));
Console.WriteLine($"crc32c(\"123456789\") = {check:x8}, computed in assembly on the {Crc32C.Path} path");
```

```text
$ dotnet crc32c-app.dll
crc32c("123456789") = e3069283, computed in assembly on the HardwareThreeWay path
$ printf 123456789 > check.txt && dotnet crc32c-app.dll check.txt
e3069283  check.txt
```

The build output holds `runtimes/<rid>/native/` for the eight native RIDs next to the program, and
the same output runs on Windows, Linux and macOS: nothing else to install. Given file names, the program reads each in 1 MiB chunks and continues the
checksum with `Crc32C.Append`, so a file never has to fit in memory.

## Versions

- **The package version** (`<Version>`) is what consumers pin, as for any package.
- **The symbol version** (`CRC32C_1.0`, from [`crc32c.map`](crc32c.md#the-exports-file)) is what
  the dynamic linker checks. A later release that adds functions puts them in a new node
  (`CRC32C_1.1`), and programs built against 1.0 keep loading.
- **Eight RIDs.** A program on a platform none of them serves (32-bit x86, FreeBSD) gets
  `DllNotFoundException` at the first call. A package that should run there too falls back to
  a managed implementation: catch `DllNotFoundException` once, or check
  `RuntimeInformation.RuntimeIdentifier` up front.

## How it is checked

[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs)
packs both projects into a local feed and checks the exact file list of each package and the
dependency line, then builds `Crc32C.App` from the packages alone, checks that its output folder
holds the RIDs' libraries under `runtimes/<rid>/native/`, and runs it on each of the five CI
build machines.
