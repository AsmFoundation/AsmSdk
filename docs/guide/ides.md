# IDEs

An `.asmproj` opens in Rider, Visual Studio and VS Code next to `.csproj` projects in the same
solution, and the IDE shows it the way it shows a `.csproj`: its files, by folder, with a build
action each. Building from the IDE runs the same `dotnet build` targets as the command line.

| What the IDE shows | Item type | Default |
|---|---|---|
| `.asm` and `.s` sources | `AsmSource` | `**/*.asm;**/*.s` (`EnableDefaultAsmItems`) |
| `.inc` include files | `AsmInclude` | `**/*.inc` (`EnableDefaultAsmItems`) |
| every other file: a linker version script, a C header, a README | `None` | `**/*` minus the asm items (`EnableDefaultNoneItems`) |

`bin/`, `obj/`, the project file and the other `DefaultItemExcludes` are never items, exactly as
in a `.csproj`.

> **Before 0.2.0** an `.asmproj` looked empty in Rider and Visual Studio, and opening a solution
> assembled and linked every asm project. 0.2.0 fixed both; the sections below say how.

## Why the files show

IDEs build their project tree from MSBuild items, but only from item types they know: their
built-in ones (`Compile`, `None`, `Content`, `EmbeddedResource`, …) and the names listed in the
project's `AvailableItemName` items. The SDK declares its two:

```xml
<AvailableItemName Include="AsmSource" />
<AvailableItemName Include="AsmInclude" />
```

Rider's MSBuild host and Visual Studio's project system both read it. It also puts `AsmSource`
and `AsmInclude` in the IDE's **Build Action** list, so moving a file between "assembled" and
"included" is a property change, as for C#.

The `None` glob is the SDK's own, not `Microsoft.NET.Sdk`'s. The managed glob removes
`**/*$(DefaultLanguageSourceExtension)` from `None` right after including `**/*`, and an
`.asmproj` has no language source extension, so that removal would take every file away again.

## Design-time builds

IDEs run *design-time builds* whenever a project loads or its file changes: a partial build that
reports references, items and compiler arguments, without producing anything. Rider calls
`Compile` and `CoreCompile` with `DesignTimeBuild=true` and `SkipCompilerExecution=true`;
Visual Studio sets the same two properties.

In an `.asmproj`, `CoreCompile` is the assemble and link step, so it does nothing when either
property is `true`, the way the C# compiler task honors `SkipCompilerExecution`. Running the
tools there would:

- make every project load slow;
- race the real build for the same `obj/` files;
- fail when a referenced project has not been built yet. Rider then reports lld's
  `cannot open .../libgrpcasm.a: no such file or directory` as a project warning, as 0.1.0 did.

Your own targets that run tools should skip design-time builds too: see
[Extending the build](extending.md).

## Building from the IDE

The IDE's **Build** runs the normal targets: NASM per out-of-date source, then the link. Before
calling MSBuild at all, Visual Studio and Rider run a *fast up-to-date check*. The SDK hands
them its inputs and outputs:

- `UpToDateCheckInput`: the `AsmSource` and `AsmInclude` items and the `ExportsFile`;
- `UpToDateCheckBuilt`: the output of each RID the project builds (none for
  `OutputType=Objects`).

So editing a `.asm` file makes the IDE build, and an unchanged project is skipped. Both item
types carry `Visible="false"`: they name files that are already shown.

**Errors you can click:** NASM prints `file:line: error:` text that MSBuild shows as plain output.
Add `<AsmFlags>-Xvc</AsmFlags>`: NASM then prints Visual Studio's format, which MSBuild turns
into diagnostics with a file and line, and the IDE's error list links them. lld's messages are
already diagnostics.

## Per IDE

- **Rider:** opens `.sln` and `.slnx` solutions with `.asmproj` projects. Rider has no NASM
  language support; a TextMate grammar for x86 or NASM syntax adds highlighting
  (**Settings | Editor | TextMate Bundles**).
- **Visual Studio:** a `.slnx` lists an `.asmproj` with the generic MSBuild project type,
  `Type="13B669BE-BB05-4DDF-9536-439F39A36129"` (see [Projects](projects.md)).
- **VS Code:** the C# Dev Kit's solution view uses the same project system as Visual Studio.
  Highlighting comes from a NASM or x86 grammar extension.

## Running and debugging

Each machine runs the outputs of its own platform, plus the x64 ones under emulation on
Windows on Arm and on Apple silicon (Rosetta 2).

- **The build of your machine's RID** runs directly: `bin/Debug/libc/win-x64/app.exe`,
  `bin/Debug/libc/osx-arm64/app`.
- **Linux builds on Windows** run in WSL (the repository's path under `/mnt/<drive>/`, or
  `wsl -e` from the repository folder) or in a container.
- **A .NET program that P/Invokes an asm library** runs on every platform the asm project
  lists: its output carries each one's library under `runtimes/<rid>/native/`.

Debug builds carry debug information (`DebugSymbols=true` in Debug):

- **Linux and macOS:** DWARF, so `gdb` and `lldb` show source lines.
- **Windows:** CodeView from NASM, which lld-link writes into a PDB beside the output, for
  Visual Studio's native debugger and WinDbg. Functions from `.s` files have names but no
  line information there, because llvm-mc writes DWARF only.

## How this is tested

`tests/AsmSdk.Tests` asks MSBuild what an IDE asks it:

- the declared item types;
- that every file of a project folder is exactly one item, with nothing from `bin/` or `obj/`;
- the up-to-date inputs and outputs.

It also replays Rider's design-time build (its targets and properties) and requires that no
tool runs and no object is written. A control build with the same targets but without the two
properties must run the tools, so the test would catch a regression.
