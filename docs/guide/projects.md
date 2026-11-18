# Projects

An `.asmproj` behaves like an SDK-style `.csproj`:

- properties go in a `<PropertyGroup>`, items in an `<ItemGroup>`;
- the properties a `.csproj` uses mean the same here: `TargetFramework(s)`, `OutputType`,
  `AssemblyName`, `DefineConstants`, `DebugSymbols`, `DebugType` and `StartupObject`;
- `Version`, `PackageId`, `Configuration`, `Directory.Build.props`,
  `Directory.Packages.props` and `nuget.config` all work as usual.

This page covers what is specific to assembly. Every setting is in the
[reference](../asmproj.md).

## A project folder

```text
Hello/
  Hello.asmproj        <Project Sdk="AsmFoundation.Sdk"> ... </Project>
  include/hello.inc    public include files: referencing projects and package consumers get them
  hello.asm            every *.asm is a source (one NASM run each)
  util/strings.asm     subfolders too; each object keeps the subfolder: obj/Debug/freestanding/asm/obj/util/strings.o
  bin/Debug/freestanding/libhello.so
```

## Target frameworks

The target framework says what the code links against, the way `net10.0` says which .NET a
`.csproj` runs on:

| `TargetFramework` | The code | Runs on |
|---|---|---|
| `freestanding` (the default) | makes its own system calls; links no C library | any x86-64 Linux kernel |
| `libc` | links glibc; an executable starts at `main` | glibc 2.34 or later |

- **Build both** with `<TargetFrameworks>freestanding;libc</TargetFrameworks>`, exactly like
  a multi-targeted `.csproj`. Each build has its own `bin/<Configuration>/<tfm>/` folder.
- **One source for both:** NASM gets `-DFREESTANDING` or `-DLIBC`:

  ```nasm
  %ifdef LIBC
      call    puts wrt ..plt          ; glibc
  %else
      mov     eax, 1                  ; write(2)
      syscall
  %endif
  ```

- **Who may reference whom** is NuGet's decision, as for a `.csproj`: a `libc` project may
  use `freestanding` projects and packages, never the reverse (`NU1201`, `NU1202`). A
  project that targets both gives each consumer its nearest build, and a .NET project the
  `libc` one.
- [Runtimes and linking](runtimes-and-linking.md) covers what each one links and how.

## Output kinds and names

| `OutputType` | Output in `bin/<Configuration>/<tfm>/` |
|---|---|
| `Library` (default) | `lib<name>.so` |
| `StaticLibrary` | `lib<name>.a` |
| `Exe` | `<name>`, with no extension |
| `Objects` | nothing; the objects stay in `obj/` |

- **`<name>` is `AssemblyName`,** which defaults to the project name, as in a `.csproj`.
  Libraries get Linux's `lib` prefix, which `-l<name>` and .NET's `[DllImport("<name>")]`
  both look for.
- **`AsmProduceStaticLibrary=true`** also writes `lib<name>.a` next to a `.so`. That suits a
  library that ships both: programs that load it (P/Invoke, `dlopen`) take the `.so`, and
  assembly projects that link statically take the `.a`.
- **The target framework folder** is appended to `bin/` and `obj/`, as in .NET:
  `bin/Debug/freestanding/`, `bin/Release/libc/`.
- **The SDK sets `OutputType` back to `Library`** after reading it, so that the .NET SDK never
  builds an apphost. Its internal `_AsmOutputKind` (`SharedLibrary`, `StaticLibrary`,
  `Executable`, `Objects`) keeps the kind; a target of your own can read it, but it is not a
  stable API.

> [!WARNING]
> **Known issue:** a `ProjectReference` to an `Objects` project fails to link with
> `cannot open <referencing project>/obj/Debug/<tfm>/asm/obj/x.o`. The object paths are passed
> relative to the wrong project. Use `StaticLibrary` instead: the linker takes only the
> archive members it needs.

## Sources and include files

- **Default items:** every `**/*.asm` is an `AsmSource`, and every `**/*.inc` is an
  `AsmInclude`. `bin/` and `obj/` are excluded. Set `EnableDefaultAsmItems=false` to list
  them yourself.
- **Name include files `.inc`.** An `.asm` file that another file `%include`s is still a
  source, so its symbols are linked twice: `duplicate symbol`. Rename it, or remove it from
  the sources with `<AsmSource Remove="helpers.asm" />`.
- **Where `%include "x.inc"` looks:**
  1. NASM tries the working directory first, which is the project folder.
  2. Then it tries the `-I` folders in order: the project's `include/`, the project folder,
     then the include folders of referenced projects and packages.

  NASM does **not** look in the folder of the file that holds the `%include`. In a source
  at `src/x86/a.asm`, `%include "b.inc"` finds neither `src/x86/b.inc` nor anything else
  there. Write `%include "src/x86/b.inc"`, or add `<AsmIncludeDirectory Include="src/x86/" />`.
- **Public include files belong in `include/`.** It is what referencing projects get on
  their include path, and what `dotnet pack` ships. A project without an `include/` folder
  packs **no** include files.

### Include folders outside the project

```xml
<ItemGroup>
  <AsmIncludeDirectory Include="../common/include/" />   <!-- the -I path -->
  <AsmInclude Include="../common/include/**/*.inc" />    <!-- makes edits rebuild -->
</ItemGroup>
```

- **Write both lines.** With only the first, editing `../common/include/x.inc` leaves the
  old objects in place: the build reports up to date, but it is stale.
- **What counts as a dependency:** the project's own `AsmInclude` items, plus the include
  folders of packages and referenced projects.

## Defines and configurations

| | Debug | Release |
|---|---|---|
| defined | `DEBUG`, and `FREESTANDING` or `LIBC` | `RELEASE`, and `FREESTANDING` or `LIBC` |
| DWARF debug information (`-g -F dwarf`) | yes | no |

- **Add symbols with `DefineConstants`,** as in C#: semicolon-separated `NAME` or
  `NAME=VALUE`, each passed to NASM as `-D`:

  ```xml
  <DefineConstants>$(DefineConstants);FAST_PATH;LEVEL=3</DefineConstants>
  ```

- **The configuration and target framework symbols are never lost.** Like C#, the SDK
  appends them after reading your project, so `<DefineConstants>FAST_PATH</DefineConstants>`
  keeps `DEBUG` too. `DisableImplicitConfigurationDefines=true` drops `DEBUG`/`RELEASE`, and
  `DisableImplicitFrameworkDefines=true` drops `FREESTANDING`/`LIBC`.
- **Debug information follows `DebugSymbols`:** `<DebugSymbols>true</DebugSymbols>` adds DWARF
  to Release builds, and `<DebugType>none</DebugType>` removes it from Debug builds. Unlike
  .NET, whose Release builds still write a PDB, a Release build carries no debug information
  unless you ask.
- **In MSBuild text, `%` starts an escape sequence.** A target that writes `%define …`
  into a file must write `%25define`; `%de` would decode to the byte `0xDE`.

## Warnings and diagnostics

NASM runs with `-w+all`, except for two warnings that position-independent code always
triggers (`reloc-rel-dword` and `reloc-abs-qword`).

- **`TreatWarningsAsErrors=true`** adds `-w+error`.
- **`<AsmFlags>-w-label-orphan</AsmFlags>`** silences one warning. The name of each warning
  is printed in brackets after it.

> [!TIP]
> By default, NASM's messages are printed as plain text above an `MSB3073: The command …
> exited with code 1` error, and `-v:q` hides them. Add `<AsmFlags>-Xvc</AsmFlags>`: NASM
> then reports `file.asm(12): error : …`, which MSBuild counts as an error or warning, and
> which IDEs link to the right line.

## Incremental builds

- **An object is rebuilt** when its source, any include file in its dependency list, or the
  project file is newer.
- **The link reruns** when an object, a link input or the project file is newer.
- **A rebuild with no changes runs no tool.**
- **Not tracked:** changes to `Directory.Build.props` and other imported files. After
  changing a define there, rebuild with `dotnet build --no-incremental`.

## Settings for every assembly project

`UsingAsmFoundationSdk` is already `true` when `Directory.Build.props` is read, so a single
file can hold settings for both the C# and the assembly projects of a solution:

```xml
<Project>
  <PropertyGroup Condition="'$(UsingAsmFoundationSdk)' == 'true'">
    <TargetFramework>libc</TargetFramework>                <!-- every asm project links glibc -->
    <AsmFlags>-Xvc</AsmFlags>                              <!-- NASM messages become MSBuild errors -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup Condition="'$(UsingAsmFoundationSdk)' == 'true'">
    <LinkerArg Include="-z defs" />                        <!-- undefined symbols fail the link -->
  </ItemGroup>
</Project>
```

`-z defs` is explained in [Runtimes and linking](runtimes-and-linking.md#production-link-options).

## Solutions

- **`.slnx`:** each `.asmproj` needs the generic MSBuild project type:

  ```xml
  <Project Path="Hello/Hello.asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />
  ```

  Without it, `dotnet build` fails with `MSB4025 … ProjectType '' not found`.
- **`dotnet sln add` refuses `.asmproj` files.** Add the line by hand.
- **Classic `.sln`:** an entry needs two parts:
  - the `Project("{13B669BE-BB05-4DDF-9536-439F39A36129}") = …` line;
  - `ProjectConfigurationPlatforms` lines for Debug and Release.

  Without the configuration lines, the build skips the project and still reports
  0 errors. Prefer `.slnx`.
