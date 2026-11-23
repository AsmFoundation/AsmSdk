# Scenario tests

> [!NOTE]
> **Not released yet.** The scenario tests are on `main` and are part of the repository's own
> test suite. They change nothing in the SDK packages: no AsmFoundation.Sdk version holds them.

A scenario is a small repository in
[`tests/scenarios`](https://github.com/AsmFoundation/AsmSdk/tree/main/tests/scenarios). It is a
`.slnx` with its `.csproj` and `.asmproj` projects, plus a `scenario.json` that lists the commands
to run on it, in order, and what each command must produce. The test suite runs every scenario as
one test. That test copies the folder, runs `dotnet build`, `run`, `test`, `publish`, `pack` and
`tool install` as a user would, then starts the programs those commands produced.

Most tests in [`tests/AsmSdk.Tests`](samples.md#how-they-are-checked) check one rule of the SDK
each. A scenario checks that the rules add up to what a user does: build an app with an asm
library and run it, unit-test asm code, publish it, pack a tool and install it. Some scenarios
put asm beside real packages that bring native files and dependencies of their own.

## The scenarios

Each test's name is the scenario's name followed by every command it runs, so a test list or a
failure line shows what the scenario does without opening its file.

| Scenario | What it proves | Commands |
|---|---|---|
| `CSharpAppAsmLibrary` | A C# app calls an asm shared library through `[LibraryImport]`. The portable build carries the library for every RID, and a publish for one RID puts it next to the app. | `dotnet build`, `dotnet run`, `dotnet publish -r {rid} -o out`, the published app |
| `AsmUnitTests` | xunit v3 tests check an asm library against .NET's `BitOperations`: popcount, leading zeros and rotation, 32 tests. They run on this machine's own build of the library, x64 or ARM64 code. | `dotnet test --solution` (Microsoft.Testing.Platform) |
| `AsmProgramLibrary` | An asm program links an asm shared library and finds it at run time through its run path or its folder. Exit status 42 comes from the library. | `dotnet build`, `dotnet run`, `dotnet publish -r {rid} -o out`, the published program |
| `NumSharpAsm` | [NumSharp](https://github.com/SciSharp/NumSharp) 0.81.0 from nuget.org beside asm. Asm kernels read the memory of `NDArray`s in place, and their sum and dot product must equal `np.sum` and `np.dot`. | `dotnet build`, `dotnet run`, `dotnet publish -r {rid} -o out`, the published app |
| `KvAsmClient` | `KvAsm.Client` 0.3.0 from nuget.org talks to a [KvAsm](https://github.com/AsmFoundation/KvAsm) server, the published `kvasm:0.3.0` image started for the scenario. It stores 100 values and reads them back, and an asm FNV-1a checks each one. | `docker run ghcr.io/asmfoundation/kvasm:0.3.0`, `dotnet build`, `dotnet run -- http://127.0.0.1:{port:kvasm}` |
| `OptunaSharpAsm` | [OptunaSharp](https://github.com/SciSharp/OptunaSharp) 1.1.0 from nuget.org beside asm. A seeded TPE study minimizes a function computed in asm, and every evaluation must equal C#'s, bit for bit. OptunaSharp brings NumSharp and the native OpenBLAS libraries, which the publish carries beside the asm library. | `dotnet build`, `dotnet run`, `dotnet publish -r {rid} -o out`, the published app |
| `CSharpStartsAsmProgram` | A C# app references an asm program and starts it as a helper process. It finds the program where the .NET host says native files are. | `dotnet build`, `dotnet run`, `dotnet publish -r {rid} -o out`, the published app |
| `AsmProgramTool` | An asm program packed as a .NET tool (`PackAsTool`). `dotnet tool install` picks this machine's RID package, and the command starts the program directly. | `dotnet pack`, `dotnet tool install --tool-path tools`, the command |
| `PlugAndPlay` | A .NET developer's start, from the `dotnet new` templates under test. `asmnet` makes a C# app, an asm library and xunit tests that build without warnings, run and test with no RID, no `global.json` pin and nothing written by hand. `dotnet sln add` takes a new `.asmproj`. `dotnet add reference` still refuses one, a .NET CLI limit this step watches for. A portable publish and a pack warn `ASM0013`; a publish for one RID does not. `asmexe` runs and `asmstatic` builds. | `dotnet new install`, `dotnet new asmnet`, `dotnet build`, `dotnet run`, `dotnet test --solution`, `dotnet new asmlib`, `dotnet sln add`, `dotnet sln list`, `dotnet add reference`, `dotnet publish`, `dotnet pack`, `dotnet publish -r {rid} -o out`, the published app, `dotnet new asmexe`, `dotnet run`, `dotnet new asmstatic`, `dotnet build` |
| `PlugAndPlayVSTest` | `asmnet --test-runner vstest`: the tests run with a plain `dotnet test` and no `global.json` entry. The name has a hyphen (`my-app`), so files and the library keep it, and the C# namespace gets `my_app`. | `dotnet new install`, `dotnet new asmnet`, `dotnet test` (VSTest), `dotnet run` |

Every asm project of the scenarios builds for all eight RIDs, from one x64 source (NASM) and one
ARM64 source (GNU syntax), except the plug-and-play ones, whose projects come from the templates
and list no RIDs: they build for the machine that runs them. Each CI runner therefore runs its own platform's code: Windows x64 and
Arm, Linux x64 and ARM64, and Apple silicon. `KvAsmClient` needs Docker that runs `linux/amd64`
containers, because KvAsm is x86-64 Linux code. That means the Linux x64 runner on CI, or a
machine with Docker Desktop. Elsewhere it is skipped, and the skip gives the reason.

## Running them

They run with the rest of the suite, after the suite's fixture has packed the SDK:

```sh
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj -c Release                                         # everything
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj -c Release --filter-class AsmSdk.Tests.ScenarioTests  # the scenarios
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj -c Release --filter-trait "Scenario=NumSharpAsm"      # one scenario
```

Each scenario runs in `artifacts/scenarios/<Name>`, a fresh copy of its folder without `bin/` or
`obj/`. What its commands write stays there for inspection. A test's log lists every command with
its exit status, duration and output. Add `--output detailed` to see each scenario's log under
its result. A failure message names the scenario, the step, the command and the unmet
expectation, and shows the output's tail, for example:

```text
KvAsmClient, step 2/2: dotnet run --project App -c Release -- http://127.0.0.1:55017
its standard output lacks "stored 100 new keys in KvAsm and read all 100 back: FNV-1a (asm) matches"
```

A `scenario.json` that cannot be read fails the same way: the test is named
`<Name>: scenario.json cannot be read`, and its message gives the file, the problem and where it
is, such as `The JSON property 'stdot' could not be mapped ... (at $.steps[0].stdot, line 4)`.

A scenario also builds where it is, opened in an IDE (`tests/scenarios/<Name>/<Name>.slnx`). It
restores the SDK from `artifacts/packages`, so the suite must have packed it once.

## Writing a scenario

A new folder under `tests/scenarios` with a `scenario.json` is a new test, and needs no C#:

```text
tests/scenarios/
  Directory.Build.props      stops MSBuild's search: the scenarios are repositories of their own
  Directory.Packages.props   every package version, pinned (central package management)
  global.json                the AsmFoundation.Sdk version under test, Microsoft.Testing.Platform
  nuget.config               AsmFoundation.* and Scenario.* from the local feed, the rest from nuget.org
  scenario.schema.json       the format, for editors
  MyScenario/
    MyScenario.slnx
    App/App.csproj
    Lib/Lib.asmproj, lib.asm, lib.s
    scenario.json
```

```json
{
  "$schema": "../scenario.schema.json",
  "description": "What the scenario proves, in a sentence or two.",
  "steps": [
    { "run": "dotnet build MyScenario.slnx -c Release",
      "files": ["App/bin/Release/net10.0/runtimes/{rid}/native/{lib:lib}"] },
    { "run": "dotnet run --project App -c Release -- 2 40",
      "stdout": ["2 + 40 = 42"] },
    { "run": "dotnet publish App -c Release -r {rid} -o out",
      "files": ["out/{exe:App}", "out/{lib:lib}"] },
    { "run": "out/App 2 40", "stdout": ["2 + 40 = 42"] }
  ]
}
```

| Property | Meaning |
|---|---|
| `description` | What the scenario proves. Required, and written at the top of the test's log. |
| `steps` | The commands, in order. Required. The first step that does not produce what it must fails the scenario. |
| `steps[].run` | The command line. Required. |
| `steps[].exitCode` | The exit status the command must return; `0` when left out. |
| `steps[].stdout` | Texts that must each appear in the command's standard output. |
| `steps[].stderr` | Texts that must each appear in the command's standard error, where the .NET CLI says why it refused a command. |
| `steps[].absent` | Texts that must appear in neither standard output nor standard error, such as a warning that must not fire. |
| `steps[].files` | Files or folders, relative to the scenario's folder, that must exist after the command. |
| `steps[].description` | Why the step is there, for the log. |
| `rids` | The build machines the scenario runs on (`win-x64`, `linux-arm64`, ...); every machine when left out. |
| `services` | Containers the steps need: `name`, `image` (with a pinned tag), `port`, and optionally `platform` (`linux/amd64` by default), `environment` and `ready`. |

The file is read strictly. An unknown property, an empty `description` or `steps`, an
unterminated quote or an unknown placeholder fails the test with the file and the reason. A
misspelt expectation is never skipped without a word.

**Commands run without a shell.** Whitespace separates arguments, and double or single quotes group
them. Backslashes, `$`, `*`, `|` and `>` mean nothing special, so a step means the same on every
OS. How the first word is run:

- **`dotnet`** gets the test environment: the isolated NuGet cache in `artifacts/test-nuget`, no
  telemetry, no MSBuild variables from the machine, and `MSBUILDDISABLENODEREUSE=1`. Node reuse is
  turned off through the environment, never by adding switches to the command.
- **Each scenario has a .NET CLI home of its own** (`DOTNET_CLI_HOME`), emptied before each run:
  `artifacts/scenarios/.cli-home/<Name>`. What `dotnet new install` and global tools put in a
  user's profile goes there, so a scenario never sees a template an earlier run or the developer
  installed, and leaves nothing behind. The first-run steps that a fresh home would take outside
  it are off (adding the tools folder to the user's `PATH`, creating a developer certificate),
  and the CLI's messages are English (`DOTNET_CLI_UI_LANGUAGE=en`), because steps compare them.
- **A path with a slash** is a file of the scenario's folder, such as `out/App` or
  `tools/asm-hello`. On Windows a path without an extension gets `.exe`, `.cmd` or `.bat`, as a
  shell would find it, and a `.cmd` runs through `cmd.exe`.
- **Any other name** is looked up on `PATH`. A step that needs a shell can run `sh -c '...'`, but
  it then runs only where `sh` exists.

**Placeholders**, in `run`, `stdout`, `stderr`, `absent` and `files`:

| Placeholder | Expands to |
|---|---|
| `{rid}` | This machine's RID: `win-x64`, `linux-arm64`, `osx-arm64`, ... |
| `{version}` | The AsmFoundation.Sdk version under test, which the templates package has too: `dotnet new install AsmFoundation.Templates@{version}` |
| `{exe:name}` | `name.exe` on Windows, `name` elsewhere |
| `{lib:name}` | This RID's shared library file: `name.dll`, `libname.so` or `libname.dylib` |
| `{port:service}` | The host port that the service's port was published on |

**Services** start before the first step with
`docker run -d --rm --platform <platform> -p 127.0.0.1::<port>`, so each gets a free host port. The
steps wait until the container's log shows `ready`. Without `ready`, they wait until the port
accepts a connection, which Docker Desktop's proxy can do before the server listens. The
containers are removed after the last step, whatever happened. A scenario with services is
skipped unless Docker runs Linux containers of their platform: Windows runners have Windows
containers only, macOS runners have no Docker, and ARM64 machines may lack x64 emulation.

**Packages** of our other projects come from nuget.org as released, at the versions pinned in
`Directory.Packages.props`. A scenario therefore tests what users install, the same on CI and on a
developer's machine. A new release reaches the scenarios when its pin is bumped. A package a
scenario packs itself must be named `Scenario.*`. It then goes to the local feed
(`Directory.Build.props`), where `nuget.config` finds it for a later step.

**Before adding a scenario:**

- Give each asm project an x64 and an ARM64 source and all eight RIDs, or list the machines it
  runs on in `rids`.
- Keep the build free of warnings: the scenarios build with `TreatWarningsAsErrors`. NuGet's
  vulnerability audit (`NU1901`-`NU1904`) is the exception, so an advisory published later
  cannot break a scenario that changed nothing. The plug-and-play scenarios are the other
  exception: their own `Directory.Build.props`, `Directory.Packages.props` and `global.json`
  stand for a user's fresh folder (empty, but for NuGet's audit, which is off). Their build
  steps check `0 Warning(s)` instead, and their `ASM0013` steps expect a warning.
- Make the expected output deterministic: fixed seeds, and integer values in floating-point
  checks, or a check the program makes itself.
- Pin every version, including images.
