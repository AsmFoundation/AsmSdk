# Projects

An `.asmproj` behaves like an SDK-style `.csproj`:

- properties go in a `<PropertyGroup>`, items in an `<ItemGroup>`;
- the properties a `.csproj` uses mean the same here: `TargetFramework(s)`, `OutputType`,
  `AssemblyName`, `DefineConstants`, `DebugSymbols`, `DebugType` and `StartupObject`;
- `Version`, `PackageId`, `Configuration`, `Directory.Build.props`,
  `Directory.Packages.props` and `nuget.config` all work as usual.

This page covers what is specific to assembly. Every setting is in the
[reference](../asmproj.md).

## Templates

> [!NOTE]
> **Not released yet.** The `AsmFoundation.Templates` package is on the main branch. It is
> released with the SDK, under the same version number.

```sh
dotnet new install AsmFoundation.Templates
```

| Template | Short name | What it creates |
|---|---|---|
| Assembly Library | `asmlib` | a shared library: x64 code in NASM (`add.asm`), ARM64 code in GNU syntax (`add.s`) |
| Assembly Console App | `asmexe` | a program on the platform's C library, which `dotnet run` builds and runs |
| Assembly Static Library | `asmstatic` | a static library with a public `include/` folder, for other asm projects |
| Assembly + .NET Solution | `asmnet` | a `.slnx` with a C# console app, the asm library it calls through `[LibraryImport]`, and xunit v3 tests |

- **`--sdk-version <version>`** sets the AsmFoundation.Sdk version the projects name on their
  first line. It defaults to the templates package's own version, which is released with the
  SDK of the same version, so a fresh install writes the newest one. `dotnet new` says when a
  newer templates package exists.
- **`--test-runner mtp-v2|vstest`** (`asmnet`) picks the test runner, as xunit's own `xunit3`
  template does. `mtp-v2`, the default, sets Microsoft.Testing.Platform in `global.json`,
  creating it at the repository root when there is none; the .NET 10 SDK's `dotnet test`
  needs that. `vstest` leaves `global.json` alone.
- **`--no-restore`** skips the restore that runs after the files are created.
- **The projects list no `RuntimeIdentifiers`**, so they build for this machine (see
  [Runtime identifiers](#runtime-identifiers)). Their comments show the line to add to ship
  more platforms.
- **Names keep their form:** `dotnet new asmnet -n my-app` writes `my-app.Native.asmproj` and
  `[LibraryImport("my-app.Native")]`, which match the library's file name, and the C#
  namespace `my_app.Tests`.

## A project folder

```text
Hello/
  Hello.asmproj        <Project Sdk="AsmFoundation.Sdk"> ... </Project>
  include/hello.inc    public include files: referencing projects and package consumers get them
  hello.asm            every *.asm is an x64 source (one NASM run per RID)
  hello.s              every *.s is a GNU-syntax source, the ARM64 code (one llvm-mc run per RID)
  util/strings.asm     subfolders too; each object keeps the subfolder: obj/Debug/freestanding/linux-x64/asm/obj/util/strings.o
  bin/Debug/freestanding/linux-x64/libhello.so
  bin/Debug/freestanding/win-x64/hello.dll
```

## Runtime identifiers

A project lists the platforms its code supports with .NET's property, and `dotnet build`
builds each one, into its own `bin/<Configuration>/<tfm>/<rid>/` folder:

```xml
<RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-arm64</RuntimeIdentifiers>
```

A project that lists none is built for the RIDs its referencing projects ask for, or for
`-r <rid>`; otherwise it fails with ASM0010. Which source builds which RID is decided by the
file name: `.asm` is NASM for the x64 RIDs, `.s` is GNU syntax for the ARM64 ones, and a
qualifier (`hello.linux.asm`, `hello.win-arm64.s`) narrows a file further.
[Platforms and runtime identifiers](platforms.md) covers all of it.

> [!NOTE]
> **Not released yet:** on the main branch, a project that lists no RIDs, and that nothing asks
> for one, builds for the build machine's own RID instead of failing with ASM0010. A referencing
> project's RIDs and `-r` still come first. What leaves the machine says when it carries that
> one RID only: `dotnet pack` of the project, and a portable `dotnet publish` or `dotnet pack`
> of a .NET project that references it, warn `ASM0013` with the line to add.
> `<NoWarn>ASM0013</NoWarn>` silences it. ASM0010 remains for a build machine that is none of
> the eight RIDs.

## Target frameworks

The target framework says what the code links against, the way `net10.0` says which .NET a
`.csproj` runs on:

| `TargetFramework` | The code | Runs on |
|---|---|---|
| `freestanding` (the default) | links no C library: Linux system calls, kernel32 on Windows | any Linux kernel of its CPU; any Windows. macOS has no freestanding programs, only libraries |
| `libc` | links the platform's C library: glibc or musl, the Universal C Runtime, libSystem; a program starts at `main` | glibc 2.34 or later, musl, Windows 10 or later, macOS 11 or later |

- **Build both** with `<TargetFrameworks>freestanding;libc</TargetFrameworks>`, exactly like
  a multi-targeted `.csproj`. Each build has its own `bin/<Configuration>/<tfm>/<rid>/`
  folders.
- **One source for both:** NASM gets `-DFREESTANDING` or `-DLIBC` (llvm-mc
  `--defsym LIBC=1`). In a Linux source:

  ```nasm
  %ifdef LIBC
      call    puts PLT                ; the C library
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

| `OutputType` | Windows | Linux | macOS |
|---|---|---|---|
| `Library` (default) | `<name>.dll` and its import library `<name>.dll.lib` | `lib<name>.so` | `lib<name>.dylib` |
| `StaticLibrary` | `<name>.lib` | `lib<name>.a` | `lib<name>.a` |
| `Exe` | `<name>.exe` | `<name>`, with no extension | `<name>` (`libc` only) |
| `Objects` | nothing; the objects stay in `obj/` | the same | the same |

All of them go to `bin/<Configuration>/<tfm>/<rid>/`. A Windows output built with debug
symbols also gets `<name>.pdb`.

- **`<name>` is `AssemblyName`,** which defaults to the project name, as in a `.csproj`.
  Libraries get the `lib` prefix on Linux and macOS, which `-l<name>` and .NET's
  `[DllImport("<name>")]` both look for. On Windows, `[DllImport("<name>")]` looks for
  `<name>.dll`.
- **`AsmProduceStaticLibrary=true`** also writes the static library next to the shared one.
  That suits a library that ships both: programs that load it (P/Invoke, `dlopen`) take the
  shared library, and assembly projects that link statically take the static one.
- **The target framework and RID folders** are appended to `bin/` and `obj/`, as in .NET:
  `bin/Debug/freestanding/linux-x64/`, `bin/Release/libc/win-arm64/`.
- **The SDK sets `OutputType` back to `Library`** after reading it, so that the .NET SDK never
  builds an apphost. Its internal `_AsmOutputKind` (`SharedLibrary`, `StaticLibrary`,
  `Executable`, `Objects`) keeps the kind; a target of your own can read it, but it is not a
  stable API.

A `ProjectReference` to an `Objects` project links its object files into the referencing
project, for each of the referencing project's RIDs. (0.1.0 and 0.2.0 passed their paths
relative to the wrong project, and the link failed.) A `StaticLibrary` does the same job, and
the linker takes only the archive members it needs.

## Sources and include files

- **Default items:** every `**/*.asm` and `**/*.s` is an `AsmSource`, and every `**/*.inc`
  is an `AsmInclude`. `bin/` and `obj/` are excluded. Set `EnableDefaultAsmItems=false` to
  list them yourself. Listing a default source again is ASM0024.

  > [!NOTE]
  > **Not released yet:** on the main branch, .NET's item types instead. Every `**/*.asm` and
  > `**/*.s` is a `Compile` item (`<Compile Remove="helpers.asm" />`; `EnableDefaultCompileItems`),
  > every other file a `None` item, and every `None` item ending in `.inc` is a dependency of
  > every source. Any other file a source reads (`%incbin` data) is a
  > `CustomAdditionalCompileInputs` item, .NET's item for more inputs of the compile. A
  > `Compile` item that is not `.asm` or `.s` is ASM0025. The old names are errors (ASM0110 to
  > ASM0112) that show the new line.
- **Name include files `.inc`.** An `.asm` file that another file `%include`s is still a
  source, so its symbols are linked twice: `duplicate symbol`. Rename it, or remove it from
  the sources with `<AsmSource Remove="helpers.asm" />`.
- **Where `%include "x.inc"` looks:**
  1. NASM tries the working directory first, which is the project folder.
  2. Then it tries the `-I` folders in order: the project's `include/`, the project folder,
     then the include folders of referenced projects and packages, then the SDK's own
     (`asmsdk.inc`, `asmsdk-gnu.inc`). `llvm-mc` gets the same folders.

  NASM does **not** look in the folder of the file that holds the `%include`. In a source
  at `src/x86/a.asm`, `%include "b.inc"` finds neither `src/x86/b.inc` nor anything else
  there. Write `%include "src/x86/b.inc"`, or add `<AsmIncludeDirectory Include="src/x86/" />`.
- **Public include files belong in `include/`.** It is what referencing projects get on
  their include path, and what `dotnet pack` ships. A project without an `include/` folder
  packs **no** include files.

### ImplicitUsings

> [!NOTE]
> **Not released yet.** On the main branch, `ImplicitUsings`, .NET's property for what every
> source gets without writing it, is on by default in an `.asmproj`. It puts the SDK's
> platform layer in front of every source, so a file is just code:
>
> ```nasm
> FUNCTION demo_add, 2                ; no %include, no default rel, no .note.GNU-stack
>     lea     eax, [ARG1 + ARG2]
>     RETURN
> ENDFUNCTION
> ```
>
> - **NASM sources** get `include/asmsdk-implicit.inc` as NASM's pre-include (`-P`):
>   `asmsdk.inc`, `default rel`, on Linux the empty `.note.GNU-stack` section, and on every OS
>   `section .text`. The last one is for Windows: NASM's COFF output writes a label defined
>   before the first section directive as an undefined symbol, so a source that names no
>   section, such as the `asmstatic` template's, would not link there (see
>   [Writing NASM](writing-nasm.md#a-source-file-skeleton)).
> - **GNU-syntax sources** get `asmsdk-gnu.inc` and the Linux note through a two-line file
>   the SDK writes in `obj/`, because llvm-mc has no pre-include option. Messages still name
>   your file and its lines.
> - **Code that already writes them keeps building:** both include files have guards, and a
>   repeated `default rel` or note changes nothing.
> - **`<ImplicitUsings>disable</ImplicitUsings>`** gives raw NASM back: for code with macros of
>   its own named like `asmsdk.inc`'s, or code assembled outside AsmSdk too.

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
  folders of packages and referenced projects. (Not released yet: the second line is
  `<None Include="../common/include/**/*.inc" />`, and the dependencies are the `None` items
  ending in `.inc` and the `CustomAdditionalCompileInputs` items.)

## Defines and configurations

| | Debug | Release |
|---|---|---|
| defined | `DEBUG`, and `FREESTANDING` or `LIBC` | `RELEASE`, and `FREESTANDING` or `LIBC` |
| debug information (DWARF; CodeView and a PDB on Windows) | yes | no |

Every build also defines its platform: `WINDOWS`, `LINUX` or `MACOS` (plus `MUSL` on
`linux-musl-x64`), and `X64` or `ARM64`. `ASMSDK_EXPORTS_FILE` marks a project with an
`ExportsFile`, and `ASMSDK_EXECUTABLE` a program.

- **Add symbols with `DefineConstants`,** as in C#: semicolon-separated `NAME` or
  `NAME=VALUE`, each passed to NASM as `-D`, and to llvm-mc as `--defsym NAME=1` (numeric
  values only):

  ```xml
  <DefineConstants>$(DefineConstants);FAST_PATH;LEVEL=3</DefineConstants>
  ```

- **The configuration and target framework symbols are never lost.** Like C#, the SDK
  appends them after reading your project, so `<DefineConstants>FAST_PATH</DefineConstants>`
  keeps `DEBUG` too. (Not released yet: a `DefineConstants` given on the command line,
  `-p:DefineConstants=X`, keeps them as well. MSBuild ignores an append to a global property,
  and in 0.3.0 the GNU-syntax platform layer then built the ELF way on Windows and macOS.) `DisableImplicitConfigurationDefines=true` drops `DEBUG`/`RELEASE`, and
  `DisableImplicitFrameworkDefines=true` drops `FREESTANDING`/`LIBC` and the platform
  symbols.
- **Debug information follows `DebugSymbols`:** `<DebugSymbols>true</DebugSymbols>` adds it
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
- **`AsmLlvmMcFlags`** passes extra arguments to llvm-mc, as `AsmFlags` does to NASM.

> [!NOTE]
> **Not released yet:** on the main branch, .NET's own warning lists take NASM's warning
> names, as they take a compiler's codes in a `.csproj`:
>
> | Property | `label-orphan` in it becomes | Effect |
> |---|---|---|
> | `NoWarn` | `-w-label-orphan` | the warning is off |
> | `WarningsAsErrors` | `-w+error=label-orphan` | that warning is an error |
> | `WarningsNotAsErrors` | `-w-error=label-orphan` | that warning stays a warning under `TreatWarningsAsErrors` |
>
> Only entries shaped like NASM's names (lower case, with hyphens) are passed on, so one list
> can hold .NET's and NuGet's codes too: `<NoWarn>$(NoWarn);NU5128;label-orphan</NoWarn>`.
> llvm-mc has no per-warning switches.

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

> [!NOTE]
> **Not released yet:** on the main branch, the assemblers' options are tracked wherever they
> are set. The SDK writes them to `obj/.../asm/assemble-options.txt`, rewritten only when they
> change, and the assemble step depends on that file, as C#'s compile depends on
> `CoreCompileInputs.cache`. A define, `NoWarn`, `ImplicitUsings` or `AsmFlags` changed in
> `Directory.Build.props` or on the command line assembles again, once.

## Reproducible builds

> [!NOTE]
> **Not released yet.** On the main branch, a Release build is the same, byte for byte, in
> every checkout folder, with any NuGet cache, and on every run: shared and static libraries,
> freestanding and `libc`, and `libc` programs, for all eight RIDs (tested: the same projects
> built in two folders of different depth, each with a NuGet cache of its own).
>
> - **Sources are passed by their path relative to the project folder,** so no object records
>   where the checkout is (ELF's file symbol, COFF's `.file` record). The SDK's own startup
>   and C library sources are assembled in their folder by file name, so no program records
>   where the NuGet cache is.
> - **NASM runs with `--reproducible`,** which leaves out the timestamp of a COFF object, kept
>   verbatim in a Windows `.lib`. lld-link links with `-Brepro`, and llvm-ar writes
>   deterministic archives.
> - **Debug information still records the folder.** NASM 3.02 has no prefix map, so a Debug
>   build, or a Release build with `DebugSymbols`, differs between folders.
> - **Messages name the source relative to the project:** `add.asm(3): error : …`. IDEs open
>   it from the project folder.

## Settings for every assembly project

`UsingAsmFoundationSdk` is already `true` when `Directory.Build.props` is read, so a single
file can hold settings for both the C# and the assembly projects of a solution:

```xml
<Project>
  <PropertyGroup Condition="'$(UsingAsmFoundationSdk)' == 'true'">
    <TargetFramework>libc</TargetFramework>                <!-- every asm project links its C library -->
    <AsmFlags>-Xvc</AsmFlags>                              <!-- NASM messages become MSBuild errors -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup Condition="'$(UsingAsmFoundationSdk)' == 'true' and $(RuntimeIdentifier.StartsWith('linux-'))">
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

  > [!NOTE]
  > **Not released yet:** on the main branch, `dotnet sln add` adds an `.asmproj` and writes
  > its `Type` itself: the SDK sets `DefaultProjectTypeGuid`, which the CLI reads.
  > `dotnet add reference` still refuses one ("incompatible targeted frameworks", because the
  > CLI does not know NuGet's target framework aliases): write the `ProjectReference` line.
- **Classic `.sln`:** an entry needs two parts:
  - the `Project("{13B669BE-BB05-4DDF-9536-439F39A36129}") = …` line;
  - `ProjectConfigurationPlatforms` lines for Debug and Release.

  Without the configuration lines, the build skips the project and still reports
  0 errors. Prefer `.slnx`.
