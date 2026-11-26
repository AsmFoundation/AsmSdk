# Programs: run, publish, deliver and pack

An asm program is an `.asmproj` with `<OutputType>Exe</OutputType>`. This page covers what
happens to it after the build:

- [`dotnet run`](#dotnet-run) starts it, on this machine, through WSL or in Docker.
- [Its shared libraries](#the-libraries-a-program-links) are copied next to it, and the
  program finds them there.
- [`dotnet publish`](#dotnet-publish) writes one folder per RID.
- [Other projects can deliver it](#a-program-as-a-dependency): a .NET app that starts it as a
  helper process, another asm program, or a package.
- [`PackAsTool`](#a-program-as-a-net-tool) makes it a .NET tool that `dotnet tool install`
  installs.

The examples use this program, which links the shared library `Calc` and is built for
three platforms:

```xml
<Project Sdk="AsmFoundation.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>libc</TargetFramework>
    <AssemblyName>hello</AssemblyName>
    <RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Calc/Calc.asmproj" />
  </ItemGroup>
</Project>
```

## dotnet run

```bash
dotnet run --project Hello -- first "second argument"
```

`dotnet run` builds the program, then starts the build this machine can run, with the
arguments after `--` unchanged. It picks the build in this order:

| Order | Runs | Example |
|---|---|---|
| 1 | this machine's own RID | `win-x64` on Windows x64 |
| 2 | another build it runs natively: on Linux, one of its CPU whose loader exists here (a `freestanding` program needs none) | `linux-musl-x64` on a glibc machine that has musl installed |
| 3 | one it emulates | `win-x64` on Windows on Arm, `osx-x64` on Apple silicon with Rosetta 2 |
| 4 | the `wasi-wasm` build, a WASI command, in wasmtime (from `PATH`), with the current folder as the program's `.` | `wasi-wasm` anywhere wasmtime is installed (see [WebAssembly](webassembly.md#wasi-programs)) |
| 5 | on Windows, a Linux build of this machine's CPU, through WSL, when the distribution has the program's loader | `linux-x64` in WSL's Ubuntu |
| 6 | a Linux build in a Docker container: `debian:stable-slim` for glibc, `alpine:3` for musl, another CPU under emulation | `linux-arm64` on an x64 machine with Docker Desktop |

`dotnet run -r <rid>` builds and runs that RID, by the same rules. When nothing here can run
any build, `ASM0091` lists what it tried.

| Property | Default | What it does |
|---|---|---|
| `AsmRunIn` | `Auto` | `Auto` (the order above), `Host` (steps 1 to 3), `Wasi` (step 4), `Wsl` or `Docker` |
| `AsmRunWslDistribution` | WSL's default | a WSL distribution to run in: `dotnet run -r linux-x64 -p:AsmRunWslDistribution=Ubuntu-24.04` |
| `AsmRunDockerImage` | `debian:stable-slim`, `alpine:3` | the image to run a Linux build in |
| `RunArguments` | | the program's arguments when the command line gives none, as in .NET |
| `RunCommand` | | your own command: when a project sets it, `dotnet run` starts that instead |

Through Docker, the program's folder is mounted at `/app` and is the working directory. The
environment variables of a `launchSettings.json` profile reach the `docker` command, not the
container. Through WSL, the working directory is the current one, as WSL maps it. On a Linux
machine, Docker runs another CPU's build only with QEMU registered in `binfmt_misc`; Docker
Desktop has it.

A library, static library or object files project stops with `ASM0090`: `dotnet run` starts
programs.

## The libraries a program links

A program that links an asm shared library (a `ProjectReference` to a library that produces
no static library, or a package whose shared library it links) gets the library next to it,
in each RID's output folder. The program finds it there:

| OS | How the program finds the library |
|---|---|
| Linux | `DT_RUNPATH` `$ORIGIN` (the program's folder) |
| macOS | `LC_RPATH` `@loader_path`; AsmSdk dylibs are named `@rpath/lib<name>.dylib` |
| Windows | the program's own folder is the first place the loader searches |

No `LD_LIBRARY_PATH` is needed. A program that links no shared library gets no run path,
and its output does not change.

`BinariesCopyPath` moves the libraries, with the same values and order of precedence as for
a .NET app: the reference's metadata, then the project's property, then `Auto`, which for a
program means its own folder:

```xml
<ProjectReference Include="../Calc/Calc.asmproj" BinariesCopyPath="lib/{rid}" />
<PackageReference Include="Acme.Crc" Version="1.0.0" BinariesCopyPath="lib/{rid}" />
```

The run path follows: `$ORIGIN/lib/linux-x64` and `@loader_path/lib/osx-arm64`. Windows has
no run path, so on a Windows RID any folder other than the program's own stops the build
with `ASM0047`. Set the value for the other RIDs only, for example with a condition on
`$(RuntimeIdentifier)`.

Which files are copied:
- Copied: the library each shared link input stands for. On Windows the program links the
  import library `calc.dll.lib`, and `calc.dll` is the file copied. Its PDB goes along when
  there is one.
- Not copied: a static library (its code is inside the program), and a link input the
  project names itself with `NativeLibrary` (a system library stays where it is).

## dotnet publish

| Command | Output |
|---|---|
| `dotnet publish -r linux-x64` | `bin/Release/libc/linux-x64/publish/` |
| `dotnet publish` | every RID the project lists, each in `bin/Release/libc/publish/<rid>/` |
| `dotnet publish -o out` | `out/<rid>/` per RID |
| `dotnet publish -r linux-x64 -o out` | `out/` itself, as in .NET |

A publish folder holds:
- the program;
- its libraries, where `BinariesCopyPath` put them in the build;
- its PDB, unless `CopyOutputSymbolsToPublishDirectory=false`;
- the project's files marked `CopyToPublishDirectory`, or only `CopyToOutputDirectory`, as
  in .NET.

`<None Update="data/settings.txt" CopyToOutputDirectory="PreserveNewest" />` puts the file
in `bin/` and in every publish folder. AsmSdk 0.3.0 ignored that metadata, and its `Update`
matched nothing.

`dotnet publish` builds Release unless you pass `-c`, as for a .NET 8 project
(`PublishRelease`). When a referencing project asks for some RIDs (the `RuntimeIdentifiers`
metadata on its `ProjectReference`), only those are published.

## A program as a dependency

### A .NET app starts it

A .NET project that references an asm program gets it with the libraries it links, as it
gets an asm library:

```xml
<ProjectReference Include="../Hello/Hello.asmproj" />
```

| .NET build | Where the program goes |
|---|---|
| portable | every RID the program lists, in `runtimes/<rid>/native/`, listed in `deps.json` |
| RID-specific (`-r linux-x64`) | that RID's build, next to the app |

`BinariesCopyPath` on the reference moves the program, and its libraries go with it,
because the program's run path is relative to the program. `BinariesCopyPath="tools/{rid}"`
gives `tools/linux-x64/hello` with `tools/linux-x64/libcalc.so`. The program's own references
do not reach the app as references of its own (see
[Private references](#a-programs-references-are-private)).

The app finds the program in the folders that the .NET host takes from `deps.json`, or in
its own folder:

```csharp
static string FindProgram(string name)
{
    string file = OperatingSystem.IsWindows() ? name + ".exe" : name;
    var folders = new List<string>();
    if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string list)
    {
        folders.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
    }

    folders.Add(AppContext.BaseDirectory);
    return folders.Select(f => Path.Combine(f, file)).FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"{file} was not delivered for this platform", file);
}

using var process = Process.Start(FindProgram("hello"), ["first", "second"]);
```

`NATIVE_DLL_SEARCH_DIRECTORIES` holds every folder that `deps.json` names for this machine's
RID, wherever `BinariesCopyPath` put the files. A RID-specific build or a NativeAOT app has
the program in its own folder (`Auto`).

### Another asm program delivers it

An asm program that references an asm program copies it, with its libraries, next to itself:

```xml
<ProjectReference Include="../Helper/Helper.asmproj" BinariesCopyPath="helpers" />
```

The helper may go into any folder, on Windows too: it is started, not loaded.

### Its package

`dotnet pack` of a program puts each RID's program and libraries in
`runtimes/<rid>/native/`. A .NET project that references the package gets them as from a
`ProjectReference`, and an asm program copies them next to itself.

`PrivateAssets="all"` on a .NET library's reference to a program puts the program inside the
library's package, the helper-process case:

```xml
<ProjectReference Include="../Hello/Hello.asmproj" PrivateAssets="all" />
```

A packed program keeps its libraries next to it. NuGet copies `runtimes/<rid>/native/`
files flat into an app built for one RID, so a run path to a subfolder would find nothing
there. A packed program whose libraries are in a subfolder stops the pack with `ASM0051`.

### A program's references are private

A program carries the libraries and programs it needs, so its `ProjectReference` and
`PackageReference` items default to `PrivateAssets="all"`:
- a project that references the program does not also get the program's libraries, which it
  would otherwise lay out a second time;
- the program's package does not depend on packages whose files it already holds.

A reference that sets `PrivateAssets` itself keeps its value.

## A program as a .NET tool

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>hello</ToolCommandName>      <!-- the program's name by default -->
  <PackageId>Acme.Hello</PackageId>
  <Version>1.0.0</Version>
</PropertyGroup>
```

`dotnet pack` writes the package kinds the .NET 10 SDK writes for a platform-specific tool:

| Package | Contents |
|---|---|
| `Acme.Hello.1.0.0.nupkg` (`DotnetTool`) | `tools/any/any/DotnetToolSettings.xml`: the command, and the package of each RID |
| `Acme.Hello.<rid>.1.0.0.nupkg` (`DotnetToolRidPackage`), one per RID | `tools/any/<rid>/`: the program, its libraries at their paths, its data files, and `DotnetToolSettings.xml` with `Runner="executable"` |

```bash
dotnet nuget push "bin/Release/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key …
dotnet tool install -g Acme.Hello
hello first second
```

`dotnet tool install` (.NET 10 or later) fetches the package of the machine's RID, with
NuGet's fallback: Alpine takes `linux-musl-x64`. The command starts the program directly.
No .NET runtime is involved, and the program's run path works from the tool store. Push the
top-level package and every RID package. `dotnet pack -r <rid>` packs that RID's package
alone.

## Diagnostics

| Code | Meaning | Fix |
|---|---|---|
| `ASM0047` | a Windows program would keep a DLL it links in a subfolder; Windows has no run path | `BinariesCopyPath="AppFolder"` (or `Auto`) on Windows RIDs |
| `ASM0048` (warning) | a program links an import library whose DLL the SDK cannot find (a package packed by AsmSdk 0.3.0) | repack the package with this SDK, or copy the DLL yourself |
| `ASM0051` | a program to pack keeps libraries in a subfolder, which NuGet would flatten | keep them next to the program (`AppFolder`, the default) |
| `ASM0052` | the tool settings file cannot be written | check the `obj/` folder |
| `ASM0053` | `PackAsTool` on a project that is not a program, or that has several target frameworks | `OutputType=Exe` and one `TargetFramework` |
| `ASM0090` | `dotnet run` of a library, static library or object files project | run a program (`OutputType=Exe`) |
| `ASM0091` | no build this machine can run, with what was tried | build a RID it runs, install wasmtime for a `wasi-wasm` build, install WSL, or start Docker |
| `ASM0092` | nothing built yet (`dotnet run --no-build`) | build first |
| `ASM0093` | an `AsmRunIn` other than `Auto`, `Host`, `Wasi`, `Wsl` or `Docker` | one of those |
