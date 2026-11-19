# Shipping to .NET through NuGet

[Crc32C](crc32c.md) is an assembly library with a C# wrapper. This page ships it the way .NET
libraries with native code ship (SkiaSharp and Microsoft.Data.SqlClient do the same): as two
packages, a native one and a managed one that depends on it. A .NET program then references
the managed package by version and never mentions assembly.

```text
Crc32C.App (net10.0)
  └─ PackageReference Samples.Crc32C.Net 1.0.0        lib/net10.0/Samples.Crc32C.Net.dll
       └─ dependency Samples.Crc32C 1.0.0             runtimes/linux-x64/native/libcrc32c.so
```

## The native package

`dotnet pack samples/Crc32C/Crc32C.asmproj -c Release` packs the `.asmproj`:

```text
Samples.Crc32C.1.0.0.nupkg
  runtimes/linux-x64/native/libcrc32c.so                the library, where .NET looks for it
  include/crc32c.inc                                    for asm projects that reference the package
  lib/netstandard1.0/_._                                says which frameworks may use it
  build/netstandard1.0/Samples.Crc32C.props             for asm consumers: include path, link input
  buildTransitive/netstandard1.0/Samples.Crc32C.props
```

- **`runtimes/linux-x64/native/`** is where NuGet puts RID-specific native files. A .NET project
  that receives the package gets the file copied into `bin/…/runtimes/linux-x64/native/`, and
  the .NET host finds it there on Linux x64 through the program's `deps.json`.
- **The same package serves asm projects:** the include file and the props make it a normal
  [package reference](../guide/references-and-packages.md#consuming-packages) for an `.asmproj`.
- **`netstandard1.0`** is the NuGet framework behind `freestanding`, the default target framework.
  Every .NET project can use it; a `libc` library would be `netstandard2.0`. See
  [Choosing a target framework](../guide/runtimes-and-linking.md#choosing-a-target-framework).

The project sets only what a package needs:

```xml
<PropertyGroup>
  <AssemblyName>crc32c</AssemblyName>
  <ExportsFile>crc32c.map</ExportsFile>
  <PackageId>Samples.Crc32C</PackageId>
  <Version>1.0.0</Version>
  <Authors>AsmFoundation samples</Authors>
  <Description>Sample: CRC-32C in x86-64 assembly (SSE4.2 + PCLMULQDQ), a freestanding shared library.</Description>
</PropertyGroup>
```

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
crc32c("123456789") = e3069283, computed in assembly on the Sse42ThreeWay path
$ printf 123456789 > check.txt && dotnet crc32c-app.dll check.txt
e3069283  check.txt
```

The build output holds `runtimes/linux-x64/native/libcrc32c.so` next to the program: nothing
else to install. Given file names, the program reads each in 1 MiB chunks and continues the
checksum with `Crc32C.Append`, so a file never has to fit in memory.

## Versions

- **The package version** (`<Version>`) is what consumers pin, as for any package.
- **The symbol version** (`CRC32C_1.0`, from [`crc32c.map`](crc32c.md#the-exports-file)) is what
  the dynamic linker checks. A later release that adds functions puts them in a new node
  (`CRC32C_1.1`), and programs built against 1.0 keep loading.
- **One RID today.** AsmSdk builds Linux x64 libraries. A package that should also run elsewhere
  falls back to a managed implementation where the native file is missing: check for
  `DllNotFoundException` once, or `OperatingSystem.IsLinux()` and
  `RuntimeInformation.ProcessArchitecture`.

## How it is checked

[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs)
packs both projects into a local feed and checks the exact file list of each package and the
dependency line, then builds `Crc32C.App` from the packages alone, checks that its output folder
holds `runtimes/linux-x64/native/libcrc32c.so`, and runs it on Linux.
