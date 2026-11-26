# The dotnet CLI with asm projects

> [!NOTE]
> **Not released yet:** this page describes the main branch. With AsmFoundation.Sdk 0.4.0 from
> nuget.org, `dotnet clean` leaves a library's `<name>.h` and `<name>.api.json`; `--ucr` builds
> the project's default RIDs and a publish or pack warns `ASM0013`; a publish profile's
> `RuntimeIdentifier` is ignored; a project that names its one `RuntimeIdentifier` itself (or
> gets it from a profile) builds its asm references for their default RIDs, so a program fails
> to link a static library of another RID; `dotnet watch` ignores `.inc` edits; `-e` and launch
> profile variables, and a profile's arguments, never reach wasmtime, WSL or Docker; NASM's
> warnings have no code, so `-nowarn:` and `-warnaserror:` cannot name them; `dotnet build -graph
> -isolate` stops at the first call into an asm project, and `-graph` alone fails for a project
> that references a `freestanding;libc` one; a tool packed with `-p:PackageId=` on the command line
> fails; and `--include-symbols` with `snupkg` writes an empty `.snupkg`. The
> [changelog](changelog.md) lists every change.

An `.asmproj` works with the `dotnet` commands the way a `.csproj` does. This page lists each
command, what it does with asm projects, and the switches the test suite runs against them on
every build machine (the scenarios under `tests/scenarios`, see
[Scenario tests](../ecosystem/scenarios.md)). The few places where the .NET CLI itself draws a
line are at the end.

## Commands

| Command | With asm projects | Checked by |
|---|---|---|
| `dotnet build` | Builds each RID the project or its users ask for. `-c`, `-f`, `-r`, the arch switch (`-a arm64`: this OS, that CPU), the os switch (`--os linux`), the use-current-runtime switch (`--ucr`: this machine's RID, as `-r` would), `-o` (one folder per RID inside it), an artifacts path (`--artifacts-path`, or `UseArtifactsOutput`), `--no-incremental`, `--no-dependencies`, `--no-restore`, `-graph` and `-graph -isolate` (MSBuild's static graph, below), `-p:`, `-bl`, `-tl`; `-nowarn:`, `-warnaserror:` and `-warnnotaserror:` with NASM's warning names (`label-orphan`), which are its warnings' codes | CliBuild, CliGraph, PlugAndPlay, WarningSwitchTests |
| `dotnet clean` | Deletes every RID build and the library's own `<name>.h` and `<name>.api.json`, with the same `-c`, `-o` or artifacts path the build had; `-graph -isolate` too | CliBuild, CliGraph |
| `dotnet restore` | Lock files (`--use-lock-file`, `--locked-mode`, `RestoreLockedMode`), `--packages`, `-r`, `--force`, `--no-cache`; the toolchain download is a `PackageDownload`, which lock files do not list | CliBuild |
| `dotnet run` | Runs a program: on this machine, its emulation, wasmtime, WSL or Docker ([Programs](programs.md#dotnet-run)). Arguments after `--`, launch profiles (`-lp`, `--no-launch-profile`; through wasmtime, WSL and Docker a profile's arguments reach the program through a launcher the SDK starts in between), `-e`, `--no-build`, `-c`, `-f`, an artifacts path, a run from the project's folder | CliRun, ProgramTests, WebAssemblyTests |
| `dotnet test` | Tests asm code from C# tests, with Microsoft.Testing.Platform or VSTest (`--filter`, `--logger`, `-r` on the test project, `--no-build`). `dotnet test` of an `.asmproj` itself finds no test project, as for any library | PlugAndPlay, PlugAndPlayVSTest, AsmUnitTests |
| `dotnet pack` | Packs the libraries and programs of every RID ([References and packages](references-and-packages.md)). `--version`, `--version-suffix`, `--no-build`, `--include-source`. `--include-symbols` with `SymbolPackageFormat=snupkg` makes no `.snupkg`: that format holds portable PDBs, and an asm package has none | CliPack |
| `dotnet publish` | Publishes a program for each RID, or the one of `-r`, `--ucr` or a publish profile (`-p:PublishProfile=<name>`: its `RuntimeIdentifier` and `PublishDir`); `-o`, `--no-build`, an artifacts path, `-graph -isolate` | CliBuild, CliGraph, ProgramTests |
| `dotnet sln` | `.sln` and `.slnx`: `add` (with the SDK's project type, `--solution-folder`, `--in-root`), `list`, `remove`, `migrate` | CliSolution, PlugAndPlay |
| `dotnet reference` | `add` from an asm project to another, `list`, `remove`. From a `.csproj` to an `.asmproj`, `add` is refused by the CLI (below) | CliSolution, PlugAndPlay |
| `dotnet package` | `add` (central package management too), `list` (`--include-transitive`, `--outdated`, `--vulnerable`, `--format json`), `remove`; `dotnet nuget why` | CliPack |
| `dotnet tool` | A program packed with `PackAsTool`: `install` (global, local, tool path), `run`, the tool's own `dotnet <command>`, `exec` and `dnx`, `list`, `update`, `uninstall`, `restore`. A `PackageId` on the command line names every RID package `<id>.<rid>` | CliPack, ToolTests |
| `dotnet new` | The AsmSdk templates (`asmlib`, `asmexe`, `asmstatic`, `asmnet`, `asmwasm`, `asmwasmapp`), and `list`, `details`, `--dry-run` | CliSolution, PlugAndPlay |
| `dotnet watch` | Builds and runs a program, and again when a `.asm`, `.s` or `.inc` file changes. Asm code is native, so it restarts the program: its notice that the project "does not support Hot Reload" is expected. With SDKs before 10.0.300 (10.0.1xx, 10.0.2xx) pass `--no-hot-reload`: their `dotnet watch` refuses a project that is not .NET 6 or later in its default mode ("Hot Reload based watching is only supported in .NET 6.0 or newer apps") | WatchTests |
| `dotnet format` | Formats a solution's C# projects and skips the `.asmproj` ones, which Roslyn cannot read; `--verify-no-changes` passes | CliSolution |
| `dotnet run app.cs` | A C# file-based app takes an asm library with `#:project <path>.asmproj`, and gets its C# bindings | CliSolution |
| `dotnet build-server shutdown` | Stops the build servers an asm build used | |

## Where the CLI draws the line

- **`dotnet reference add` into a `.csproj`:** the CLI reads the referenced project's
  `TargetFramework` text and does not know the asm names (`freestanding`, `libc`), so it refuses
  the reference ("incompatible targeted frameworks"), although NuGet and the build accept it
  (dotnet/sdk#57317). Write the `ProjectReference` line yourself.
- **A launch profile's `commandLineArgs`** reach a program run through wasmtime, WSL or Docker
  only through the SDK's launcher, because the CLI applies a profile's arguments only to a command
  that has none of its own ([Programs](programs.md#dotnet-run)). On Windows the launcher is built
  with the bundled toolchain: with `AsmToolchain=System` there is none, so pass the arguments
  after `--` there. `-e` reaches all three from SDK 10.0.300 on, the first band to tell a build
  its `-e` variables (WSL through the launcher: only the .NET 11 SDK's CLI applies the `WSLENV`
  a build asks for). With the .NET 10.0.4xx SDKs, a comment or trailing comma inside the profile
  that runs drops it ([Programs](programs.md#dotnet-run)).
- **llvm-mc's warnings** (GNU-syntax `.s` sources: ARM64, WebAssembly) have no names, so they
  reach MSBuild without a code, and `-nowarn:` or `-warnaserror:` cannot pick one out; a plain
  `-warnaserror` covers them. NASM's warnings carry their names as codes
  ([Projects](projects.md)).
- **Static graph builds** (`dotnet build -graph`, and `-graph -isolate`, where a project may call
  only the project instances the graph predicted, with the targets it was told of): restore
  first and build with `--no-restore`, because NuGet's restore fails `-isolate` for any project,
  C# alone included. A build with a global RuntimeIdentifier (`-r`) fails `-isolate` at .NET's
  own framework negotiation for a C# reference (also C# alone; measured with SDK 11.0.100); asm
  references pass. In a graph build an asm reference builds into its own `bin/`, not the
  referencing program's `-o` folder.
- **`dotnet run` has no use-current-runtime switch**: `dotnet run --ucr` passes `--ucr` to the
  program. Use `-r <rid>`.
- **The .NET SDK bands differ** (measured with 10.0.101, 10.0.404 and 11.0.100): `dotnet run -e`
  reaches wasmtime, WSL and Docker from SDK 10.0.300 on; `dotnet watch` needs `--no-hot-reload`
  before 10.0.300; the 10.0.4xx SDKs' `dotnet run` drops a launch profile that holds a comment or
  a trailing comma inside it ([Programs](programs.md#dotnet-run),
  [Troubleshooting](troubleshooting.md)).
