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

Plugin 0.1.0 documents AsmFoundation.Sdk 0.1.0. Each behavior it states was reproduced by
building with that release and its bundled NASM and `ld.lld`. The same knowledge is on this
site:

- the project model;
- the complete property, item and target reference;
- the linking rules, with the exact error each prevents;
- .NET, Python and C interop;
- the toolchain, CI and troubleshooting.

## The scripts

The scripts are in the plugin's `skills/asmsdk/scripts/` folder. They need only Python 3.8
or later, with no packages.

### new_asmproj.py

Scaffolds a project that builds on the first `dotnet build`:

```sh
python new_asmproj.py Hello --kind library --lib-name hello --sln App.slnx
python new_asmproj.py Tool --kind exe --runtime libc
python new_asmproj.py Crc --kind static --package-id Acme.Crc --version 1.2.0
```

- **What it writes:**
  - the `.asmproj`;
  - a public include file in `include/`;
  - a starter source that follows every linking rule;
  - the `global.json` entry, when missing;
  - the `.slnx` line with the project type GUID.
- **It never overwrites a file.** An existing `global.json` gains only the `msbuild-sdks`
  entry, and an existing `.slnx` gains only the new line.

### check_asmproj.py

Lints projects and sources for the mistakes that break builds or outputs, with the file,
line and fix for each. It exits 1 when it finds an error.

```sh
python check_asmproj.py .            # every .asmproj and .slnx under the folder
python check_asmproj.py App.slnx --json
```

| Codes | What they catch |
|---|---|
| `ASMPIC001`–`005` | absolute addressing without `default rel`; calls and data access across objects without `wrt ..plt` / `wrt ..got`; absolute addresses in `.text`; `dq label` in read-only sections |
| `ASMSRC005`–`013` | an unwritable `.data.rel.ro`; a missing `.note.GNU-stack`; untyped exports; keyword symbol names; `%include` paths NASM won't find; an `.asm` included and assembled twice; a missing entry symbol; symbols nothing defines (including libc functions outside the stub); `hidden` written after the size, which NASM ignores |
| `ASMPRJ000`–`012` | `AsmDefineConstants` dropping `DEBUG`; include folders outside the project that don't rebuild; packages without include files; `$ORIGIN` that `/bin/sh` empties; a bad `AsmToolchain`; recommendations (`-Xvc`, `-z defs`) |
| `ASMREF000`–`002` | missing references; a freestanding executable referencing a shared-only library; references to `Objects` projects (the 0.1.0 issue) |
| `ASMSLN001`–`003` | no `msbuild-sdks` entry; `.slnx` entries without the type GUID; an SDK version other than the one the checks encode |

On AsmSdk's own samples it reports no error and no warning.

### inspect_elf.py

Summarizes an output, using the `llvm-readobj` from the toolchain package, which it finds in
the NuGet cache by itself. It reports:

- the ELF type: shared object, PIE or static executable;
- `SONAME`, `NEEDED`, `RUNPATH` and the interpreter;
- the exported and imported symbols, with their types and sizes;
- whether the stack is executable;
- for an archive, its members.

```sh
python inspect_elf.py bin/Debug/freestanding/libhello.so
python inspect_elf.py bin/Release/libc/libcalc.so --expect-type shared --expect-export calc_sum --expect-needed libc.so.6
```

With `--expect-…` options it exits 1 when an expectation fails, which makes it a release
gate in CI.

## How well it works

The plugin ships with an evaluation (`claude plugin eval`) that runs Claude with and without
it. What Claude wrote in each run was then built with the real SDK, and the outputs were run
on Linux. Results from 2026-11-17:

| Measure | With the plugin | Without |
|---|---|---|
| Requests about AsmSdk that loaded the skill (Sonnet 5.5, 30 runs) | 30 / 30 | — |
| Near-miss requests that left it alone (CMake, MASM, C intrinsics, …; 30 runs) | 30 / 30 | — |
| Agent-written projects that built and passed their checks | **12 / 12** | **4 / 12** |
| Mean time per task | 232 s | 444 s |

The full results, including what every run wrote, are in the marketplace's
[benchmarks](https://github.com/AsmFoundation/AsmClaudeMarketplace/tree/main/plugins/asmsdk/evals/benchmarks).
