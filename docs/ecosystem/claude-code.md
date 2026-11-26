# Claude Code plugin

The `asmsdk` plugin teaches [Claude Code](https://code.claude.com) this SDK: projects,
references, packages, the NASM rules that decide whether code links, the toolchain, CI and
releases. It comes with three scripts that also work on their own. It is published in the
AsmFoundation's plugin marketplace,
[AsmClaudeMarketplace](https://github.com/AsmFoundation/AsmClaudeMarketplace).

```text
/plugin marketplace add AsmFoundation/AsmClaudeMarketplace
/plugin install asmsdk@asmfoundation
```

Claude loads the skill on its own when a task involves an `.asmproj`, `AsmFoundation.Sdk`,
NASM built with `dotnet build`, assembly NuGet packages, or a linker error from such a build.
You can also call it by name: `/asmsdk:asmsdk`.

## What it knows

Plugin 0.4.0 documents AsmFoundation.Sdk 0.4.0: ten runtime identifiers, NASM for the x64
ones, GNU syntax for ARM64 and LLVM's WebAssembly assembly for `browser-wasm` and `wasi-wasm`,
the C library of every OS, .NET consumers by RID with the C# bindings the SDK writes from
`PROTO` and `STRUCT` lines, the C header, programs (`dotnet run`, `dotnet publish`,
`PackAsTool`) and the `dotnet new` templates. Each behavior it states was reproduced by building
with an AsmSdk release and its bundled tools, or is verified by AsmSdk's own tests on five build
machines; what each output format's NASM accepts was measured directive by directive. For a
0.1.0, 0.2.0 or 0.3.0 project, it names each removed or renamed setting's replacement. The same
knowledge is on this site:

- the project model, the templates, and the platforms and runtime identifiers;
- the complete property, item and target reference;
- the linking rules, with the exact error each prevents;
- .NET (the generated bindings), Python and C interop, and programs;
- WebAssembly;
- the toolchain, CI and troubleshooting.

## The scripts

The scripts are in the plugin's `skills/asmsdk/scripts/` folder. They need only Python 3.8
or later, with no packages.

### new_asmproj.py

Scaffolds a project that builds every RID it lists on the first `dotnet build`:

```sh
python new_asmproj.py Hello --kind library --assembly-name hello --sln App.slnx
python new_asmproj.py Tool --kind exe --framework libc
python new_asmproj.py Crc --kind static --package-id Acme.Crc --version 1.2.0 --rids "linux-x64;win-x64"
```

- **What it writes:**
  - the `.asmproj`, with `RuntimeIdentifiers`: by default every native RID the kind can build
    (all eight; a freestanding program all but macOS, which has none); `--rids
    "browser-wasm;wasi-wasm"` writes a WebAssembly library, and `--kind exe --rids wasi-wasm` a
    WASI command;
  - a public include file in `include/`;
  - starter sources that follow every linking rule on every OS: a NASM file for the x64 RIDs
    and a GNU-syntax file for the ARM64 RIDs, whose platform layer (`asmsdk.inc`,
    `asmsdk-gnu.inc`) `ImplicitUsings` puts in front. A library's functions carry the same
    `PROTO` lines in every source, so the C# bindings and the C header come out. A freestanding
    program makes Linux system calls and calls kernel32 on Windows; a libc one calls `puts`,
    which takes no variadic arguments (Apple's ARM64 passes those on the stack);
  - the `global.json` entry, when missing;
  - the `.slnx` line with the project type GUID.
- **Verified:** every kind and framework built for all its RIDs with 0.4.0, and the Windows
  programs ran (with 0.3.0, the programs ran on Windows x64, Ubuntu, Alpine and Linux ARM64).
- **It never overwrites a file.** An existing `global.json` gains only the `msbuild-sdks`
  entry, and an existing `.slnx` gains only the new line.

### check_asmproj.py

Lints projects and sources for the mistakes that break builds or outputs, with the file,
line and fix for each. It exits 1 when it finds an error.

It judges each project by the rules of the SDK version it pins: on 0.4.0, `ImplicitUsings`
(no missing `default rel` or `.note.GNU-stack` is reported, as the SDK writes them), the
renamed items, and the build machine's RID as the default. It judges each source for the
platforms it is built for: the project's `RuntimeIdentifiers`
(a project that lists none takes the RIDs of the asm projects that reference it), the
source's file-name qualifier, chosen the way the SDK chooses, and the `%ifdef WINDOWS` /
`LINUX` / `MACOS` guards around each line. A directive that is right on Linux and stops
macOS's NASM is an error there, and a warning when the RIDs are only assumed. With the SDK
package in the NuGet cache, it also checks C library calls against each OS's list.

```sh
python check_asmproj.py .            # every .asmproj and .slnx under the folder
python check_asmproj.py App.slnx --json
```

| Codes | What they catch |
|---|---|
| `ASMPIC001`–`005` | per OS: absolute addressing without `default rel` (Linux `.so`/PIE, macOS's NASM, Windows' `ASM0080`); calls and data of *another* library from a Linux `.so` without `PLT` / `wrt ..got` (within one library, `-Bsymbolic` binds them); absolute addresses in `.text` (Linux, macOS's `ASM0081`); `dd label` and `dq label` in read-only sections |
| `ASMSRC005`–`022` | an unwritable `.data.rel.ro`; a missing `.note.GNU-stack` on Linux; untyped exports in Linux-only files; keyword symbol names; `%include` paths NASM won't find; an `.asm` included and assembled twice; a missing entry symbol; symbols nothing defines (including C library functions outside each OS's list); `hidden` written after the size; the source selection's errors (`ASM0020`, `ASM0021`, `ASM0023`, `ASM0024`); sections, symbol types and `wrt` forms a platform's NASM output rejects (`.note.GNU-stack` on macOS, `:function` on macOS, `hidden` on Windows and macOS, `wrt ..plt` off Linux); a plain `global` that a Windows DLL does not export, unless the other OSes hide it (then it is internal, as meant); a symbol internal on one OS and exported on another |
| `ASMPRJ000`–`024` | a setting that 0.2.0, 0.3.0 or 0.4.0 removed or renamed, with its replacement (`AsmSource`, `AsmInclude`, `EnableDefaultAsmItems`, `AsmLinkLibrary`: `ASM0109`–`ASM0112`); no `RuntimeIdentifiers` (0.4.0: one RID shipped, `ASM0013`; 0.3.0: `ASM0010`); a RID AsmSdk does not build (`ASM0011`); WebAssembly's project rules (a wasm RID before 0.4.0, a browser program, a libc module, a define with a value, an export pattern); a freestanding macOS program (`ASM0015`); a target framework other than `freestanding` or `libc` (`ASM0003`); two projects of one restore graph with the same `PackageId` ("Ambiguous project name"); include folders outside the project that don't rebuild; packages without include files; `$ORIGIN` that `/bin/sh` empties; a bad `AsmToolchain`; recommendations (`-z defs`; `-Xvc` is now the SDK's own) |
| `ASMREF000`–`003` | missing references; a freestanding Linux executable referencing a shared-only library; a freestanding project referencing libc-only code (`NU1201`) |
| `ASMSLN001`–`003` | no `msbuild-sdks` entry; `.slnx` entries without the type GUID; an SDK version other than the one the checks encode |
| `ASMWASM001`–`016` | llvm-mc's measured traps in a `.wasm.s` source: an assembler constant, a data label without `ENDDATA`, a bare `.text`, `PROTO` or a function macro inside a function, a function without `ENDFUNCTION`, an untyped `select`, `return_call` where .NET links the code, `STRUCT`, a call to a function llvm-mc has not seen, unaligned WASI data, a `PROTO` that lowers to another wasm type or names no exported function, a float constant llvm-mc refuses or rounds to f32 unannounced (with the literal to write instead) |

On AsmSdk's own samples it reports no error and no warning (checked with 0.4.0), and each of
AsmSdk's test projects that must fail is reported with the SDK's own error.

### inspect_output.py

Summarizes an output of any RID, using the `llvm-readobj` from the toolchain package, which it
finds in the NuGet cache by itself. It reports:

- ELF (Linux): shared object, PIE or static executable; `SONAME`, `NEEDED`, `RUNPATH` and
  the interpreter; the exported and imported symbols, with their types and sizes; whether the
  stack is executable;
- PE (Windows): DLL or program; the exports, which only a `FUNCTION` directive or the
  `ExportsFile` gives a DLL; the imported DLLs and functions; ASLR and DEP;
- Mach-O (macOS): dylib or program; the install name and needed libraries; the exported and
  imported symbols, by their C names;
- WebAssembly, read in Python: module, WASI command, object or archive; exports, imports,
  memory, custom sections and the `asmapi` records the SDK's bindings come from;
- for a static library of any OS, its members and the symbols each defines.

```sh
python inspect_output.py bin/Debug/freestanding/linux-x64/libhello.so
python inspect_output.py bin/Release/libc/win-x64/calc.dll --expect-type dll --expect-export calc_sum --expect-needed ucrtbase.dll
python inspect_output.py bin/Release/libc/osx-arm64/libcalc.dylib --expect-type dylib --expect-export calc_sum
```

With `--expect-…` options it exits 1 when an expectation fails, which makes it a release
gate in CI.

## How well it works

The plugin ships with an evaluation (`claude plugin eval`) that runs Claude with and without
it. What Claude wrote in each run was then built with the real SDK, and the outputs were run
on Linux. Results from 2026-11-17, measured with plugin 0.1.0 and AsmSdk 0.1.0 (not re-run
since):

| Measure | With the plugin | Without |
|---|---|---|
| Requests about AsmSdk that loaded the skill (Sonnet 5.5, 30 runs) | 30 / 30 | — |
| Near-miss requests that left it alone (CMake, MASM, C intrinsics, …; 30 runs) | 30 / 30 | — |
| Agent-written projects that built and passed their checks | **12 / 12** | **4 / 12** |
| Mean time per task | 232 s | 444 s |

The full results, including what every run wrote, are in the marketplace's
[benchmarks](https://github.com/AsmFoundation/AsmClaudeMarketplace/tree/main/plugins/asmsdk/evals/benchmarks).

Plugin 0.4.0's tasks name this SDK version: one ports a Linux-only library to Windows and
macOS, and one writes a WebAssembly library with its `PROTO` line. Its build check builds every
RID a task names, reads the exports of each library (ELF, PE, Mach-O and WebAssembly), runs the
Linux outputs, and runs the wasm module in Node. A right answer and wrong ones per task, kept
with the suite, prove the graders and the build check before they judge a run: with the 0.4.0
packages, all 12 passed or failed as their names say.
