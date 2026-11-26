# The dotnet CLI with asm projects

> [!NOTE]
> **Not released yet:** this page describes the main branch. With AsmFoundation.Sdk 0.4.0 from
> nuget.org, `dotnet clean` leaves a library's `<name>.h` and `<name>.api.json`; `--ucr` builds
> the project's default RIDs and a publish or pack warns `ASM0013`; a publish profile's
> `RuntimeIdentifier` is ignored; a project that names its one `RuntimeIdentifier` itself (or
> gets it from a profile) builds its asm references for their default RIDs, so a program fails
> to link a static library of another RID; `dotnet watch` ignores `.inc` edits; `-e` and launch
> profile variables never reach wasmtime, WSL or Docker; a tool packed with `-p:PackageId=` on
> the command line fails; and `--include-symbols` with `snupkg` writes an empty `.snupkg`. The
> [changelog](changelog.md) lists every change.

An `.asmproj` works with the `dotnet` commands the way a `.csproj` does. This page lists each
command, what it does with asm projects, and the switches the test suite runs against them on
every build machine (the scenarios under `tests/scenarios`, see
[Scenario tests](../ecosystem/scenarios.md)). The few places where the .NET CLI itself draws a
line are at the end.

## Commands

| Command | With asm projects | Checked by |
|---|---|---|
| `dotnet build` | Builds each RID the project or its users ask for. `-c`, `-f`, `-r`, the arch switch (`-a arm64`: this OS, that CPU), the os switch (`--os linux`), the use-current-runtime switch (`--ucr`: this machine's RID, as `-r` would), `-o` (one folder per RID inside it), an artifacts path (`--artifacts-path`, or `UseArtifactsOutput`), `--no-incremental`, `--no-dependencies`, `--no-restore`, `-graph`, `-p:`, `-bl`, `-tl` | CliBuild, PlugAndPlay |
| `dotnet clean` | Deletes every RID build and the library's own `<name>.h` and `<name>.api.json`, with the same `-c`, `-o` or artifacts path the build had | CliBuild |
| `dotnet restore` | Lock files (`--use-lock-file`, `--locked-mode`, `RestoreLockedMode`), `--packages`, `-r`, `--force`, `--no-cache`; the toolchain download is a `PackageDownload`, which lock files do not list | CliBuild |
| `dotnet run` | Runs a program: on this machine, its emulation, wasmtime, WSL or Docker ([Programs](programs.md#dotnet-run)). Arguments after `--`, launch profiles (`-lp`, `--no-launch-profile`), `-e`, `--no-build`, `-c`, `-f`, an artifacts path, a run from the project's folder | CliRun, ProgramTests, WebAssemblyTests |
| `dotnet test` | Tests asm code from C# tests, with Microsoft.Testing.Platform or VSTest (`--filter`, `--logger`, `-r` on the test project, `--no-build`). `dotnet test` of an `.asmproj` itself finds no test project, as for any library | PlugAndPlay, PlugAndPlayVSTest, AsmUnitTests |
| `dotnet pack` | Packs the libraries and programs of every RID ([References and packages](references-and-packages.md)). `--version`, `--version-suffix`, `--no-build`, `--include-source`. `--include-symbols` with `SymbolPackageFormat=snupkg` makes no `.snupkg`: that format holds portable PDBs, and an asm package has none | CliPack |
| `dotnet publish` | Publishes a program for each RID, or the one of `-r`, `--ucr` or a publish profile (`-p:PublishProfile=<name>`: its `RuntimeIdentifier` and `PublishDir`); `-o`, `--no-build`, an artifacts path | CliBuild, ProgramTests |
| `dotnet sln` | `.sln` and `.slnx`: `add` (with the SDK's project type, `--solution-folder`, `--in-root`), `list`, `remove`, `migrate` | CliSolution, PlugAndPlay |
| `dotnet reference` | `add` from an asm project to another, `list`, `remove`. From a `.csproj` to an `.asmproj`, `add` is refused by the CLI (below) | CliSolution, PlugAndPlay |
| `dotnet package` | `add` (central package management too), `list` (`--include-transitive`, `--outdated`, `--vulnerable`, `--format json`), `remove`; `dotnet nuget why` | CliPack |
| `dotnet tool` | A program packed with `PackAsTool`: `install` (global, local, tool path), `run`, the tool's own `dotnet <command>`, `exec` and `dnx`, `list`, `update`, `uninstall`, `restore`. A `PackageId` on the command line names every RID package `<id>.<rid>` | CliPack, ToolTests |
| `dotnet new` | The AsmSdk templates (`asmlib`, `asmexe`, `asmstatic`, `asmnet`, `asmwasm`, `asmwasmapp`), and `list`, `details`, `--dry-run` | CliSolution, PlugAndPlay |
| `dotnet watch` | Builds and runs a program, and again when a `.asm`, `.s` or `.inc` file changes. Asm code is native, so it restarts the program: its notice that the project "does not support Hot Reload" is expected | WatchTests |
| `dotnet format` | Formats a solution's C# projects and skips the `.asmproj` ones, which Roslyn cannot read; `--verify-no-changes` passes | CliSolution |
| `dotnet run app.cs` | A C# file-based app takes an asm library with `#:project <path>.asmproj`, and gets its C# bindings | CliSolution |
| `dotnet build-server shutdown` | Stops the build servers an asm build used | |

## Where the CLI draws the line

- **`dotnet reference add` into a `.csproj`:** the CLI reads the referenced project's
  `TargetFramework` text and does not know the asm names (`freestanding`, `libc`), so it refuses
  the reference ("incompatible targeted frameworks"), although NuGet and the build accept it
  (dotnet/sdk#57317). Write the `ProjectReference` line yourself.
- **A launch profile's `commandLineArgs`** do not reach a program run through wasmtime, WSL or
  Docker: the CLI applies a profile's arguments only to a command that has none of its own. Pass
  them after `--`. A profile's variables reach wasmtime and Docker but not WSL; `-e` reaches all
  three with the .NET 11 SDK ([Programs](programs.md#dotnet-run)).
- **`-nowarn:<name>` and `-warnaserror:<name>`** on the command line act on MSBuild warning codes,
  and the assemblers' warnings have none: use the `NoWarn`, `WarningsAsErrors` and
  `WarningsNotAsErrors` properties, which AsmSdk passes to NASM by warning name
  ([Projects](projects.md)). A plain `-warnaserror` covers them.
- **`dotnet build -graph -isolate`** (MSBuild's isolated graph builds) stops a .NET project that
  references an asm project with `MSB4252`: the SDK asks an asm project for its outputs with the
  RID of the consumer, which the static graph does not predict. `-graph` alone works.
- **`dotnet run` has no use-current-runtime switch**: `dotnet run --ucr` passes `--ucr` to the
  program. Use `-r <rid>`.
