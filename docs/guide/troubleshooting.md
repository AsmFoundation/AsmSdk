# Troubleshooting

Find the message, then apply the fix. Unless a row says otherwise, every message below
was reproduced with AsmFoundation.Sdk 0.1.0, NASM 3.02 and `ld.lld` 23.1.5, and is
written with the 0.2.0 property names. The `ASM0003`, `ASM0100` to `ASM0108` and
`Ambiguous project name` rows, and the target framework rows, come from 0.2.0.

**First, get the real message.** By default, a failed NASM run surfaces as:

```text
<sdk>/targets/AsmFoundation.Build.targets(174,5): error MSB3073: The command ""…/nasm.exe" -f elf64 …" exited with code 1.
```

NASM's own line (`…/a.asm:5: error: …`) is printed just above it, at the default
verbosity. `-v:q` hides it.

- Add `<AsmFlags>-Xvc</AsmFlags>` (best in `Directory.Build.props`). NASM's messages then
  become MSBuild errors and warnings with file and line:
  `a.asm(5): error : instruction expected, found 'movx eax'`.
- `lld` errors are already recognized: `lld : error : …`.
- The target line number in `MSB3073` tells you which step failed:

  | Line in `AsmFoundation.Build.targets` | Step |
  |---|---|
  | 174 | assembling a source |
  | 257 | linking a shared library |
  | 260 | linking a freestanding executable |
  | 263 | linking a libc executable |
  | 267 | archiving |
  | 203 and 204 | building the libc stub |
  | 222 | building `crt1` |

## Restore and SDK resolution

| Message | Cause | Fix |
|---|---|---|
| `MSB4236: The SDK 'AsmFoundation.Sdk' specified could not be found` | no feed has that version, or `global.json` lacks `msbuild-sdks` | add `{"msbuild-sdks": {"AsmFoundation.Sdk": "0.2.0"}}`; add the feed to `nuget.config` (folder feeds work) |
| `ASM0003: TargetFramework 'netstandard2.0' is not an AsmSdk target framework` | a `TargetFramework` other than `freestanding` or `libc`, such as 0.1.0's hidden `netstandard2.0` | `freestanding` or `libc`, or `TargetFrameworks=freestanding;libc` |
| `ASM0100` to `ASM0107: … was removed in AsmFoundation.Sdk 0.2.0`, then `ASM0108` | a 0.1.0 setting (`AsmLibraryName`, `AsmRuntime`, `AsmDefineConstants`, `AsmDefine`, `AsmDebugInfo`, `AsmEntryPoint`, `AsmLinkFlags`, `AsmOutputKind`) | the replacement each error names; the [changelog](changelog.md) has the whole table |
| `Ambiguous project name 'x'` (restore) | two projects of one restore graph share an `AssemblyName`, for example a static and a shared build of the same library (`libgrpcasm.a`, `libgrpcasm.so`). `PackageId` defaults to `AssemblyName`, as in .NET, and NuGet names projects by it. 0.1.0's `AsmLibraryName` did not touch it, so this appears when migrating. | give each project its own `<PackageId>`; the file names still follow `AssemblyName` |
| `NU1201: Project X is not compatible with netstandard1.0 (.NETStandard,Version=v1.0)` | a `freestanding` project references a `libc`-only project: freestanding code cannot link glibc code | make the reference `freestanding` (or `TargetFrameworks=freestanding;libc`), or make this project `libc` |
| `NU1202: Package X is not compatible with netstandard1.0` | the same with a `libc`-only package | the same, in the package's project |
| `NU1101: Unable to find package AsmFoundation.Toolchain.<rid>` | no toolchain package for this build machine (`osx-x64`, `linux-arm64`, `win-arm64`), or the feed holding it is not mapped | see [The toolchain](../toolchain/index.md); check the package source mapping |
| `NU1101: Unable to find package <your package>` while the `.nupkg` is in a folder feed | package source mapping, possibly from a parent `nuget.config`, routes the id elsewhere | map the id's pattern to that feed; start the mapping with `<clear />` |
| `ASM0001: The bundled toolchain was not found in '…'` | built with `--no-restore` on a clean cache, cleaned caches, or a different `NUGET_PACKAGES` at build time | `dotnet restore`, then build; keep `NUGET_PACKAGES` the same |
| `ASM0002: AsmToolchain must be 'Bundled' or 'System'` | typo | fix the value |
| `MSB4025 … ProjectType '' not found` on `dotnet build x.slnx` | an `.asmproj` entry without a type | `<Project Path="…asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />` |
| `… has an unknown project type and cannot be added to the solution file` from `dotnet sln add` | the .NET CLI does not know the `.asmproj` extension | add the entry by hand (`.slnx`: one line with the type GUID), or use `new_asmproj.py --sln` |
| a classic `.sln` build reports 0 errors but never builds the asm project | the `.sln` lists the project but has no `ProjectConfigurationPlatforms` lines for it | add `{guid}.Debug\|Any CPU.ActiveCfg` / `.Build.0` lines (and Release), or move to `.slnx` |
| a changed SDK has no effect while you develop the SDK itself | MSBuild nodes cached the old files | build with `-nodeReuse:false`, and use a fresh `NUGET_PACKAGES` |

## Assembling (NASM)

| Message | Cause | Fix |
|---|---|---|
| `warning: implicit DEFAULT ABS is deprecated [-w+implicit-abs-deprecated]` (an error with `TreatWarningsAsErrors`) | a memory operand in a file without `default rel` | add `default rel` at the top of every source |
| `warning: 32-bit absolute section-crossing relocation [-w+reloc-abs-dword]` | absolute addressing, usually the same root cause | `default rel` |
| `error: unable to open include file 'b.inc'` | NASM looks in the project folder and the `-I` folders, never beside the including file | `%include "sub/b.inc"`, or `<AsmIncludeDirectory Include="sub/" />`; for a package or reference, check it ships the file under `include/` |
| `error: label, instruction or prefix expected at start of line, found 'rel'` | a symbol named like a keyword or mnemonic (`rel`, `wait`, `syscall`) | write it as `$rel` in the definition and in every reference |
| `error: DEBUG is not defined` (from your own `%error`) | `DisableImplicitConfigurationDefines=true`, or a Release build (it defines `RELEASE`) | remove the property; test `%ifdef DEBUG` only for Debug-only code |
| a `%define` written by an MSBuild target came out as `Þfine …` | `%de` in an MSBuild string is an escape for byte `0xDE` | write `%25define` |
| `warning: label 'x' alone on a line without a colon might be in error [-w+label-orphan]` | a missing `:` or a typo | add the colon; or `-w-label-orphan` |
| `warning: dword exceeds bounds [-w+number-overflow]` | the constant is too large for its size | fix the size or the constant |
| `nasm: warning: unknown warning name: reloc-rel-dword` (System toolchain) | NASM 2.x does not know NASM 3's warning names | harmless; or install NASM 3 |

## Linking (`ld.lld`)

| Message | Cause | Fix |
|---|---|---|
| `relocation R_X86_64_PC32 cannot be used against symbol 'f'; recompile with -fPIC` | a `.so` calls a function from another object or library directly | `call f wrt ..plt`; or define `f` as `global f:function hidden` |
| `relocation R_X86_64_PC32 cannot be used against symbol 'stdout'` | a `.so` reads external data directly | `mov rax, [rel stdout wrt ..got]` then `mov rax, [rax]` |
| `relocation R_X86_64_32S cannot be used against local symbol; recompile with -fPIC` | absolute 32-bit addressing (no `default rel`) in a `.so` or PIE | `default rel` |
| `relocation R_X86_64_64 cannot be used against local symbol; recompile with -fPIC` | `mov rax, label`, or `dq label` in a read-only section, in a `.so` or PIE | `lea rax, [label]`; offset tables in `.rodata`, or `dq` in `.data` / a `write` section |
| `attempted static link of dynamic object <path>/libx.so` | a freestanding `Exe` was given a `.so` (a referenced `Library`, or a package with `AsmPackageLinkKind=Shared`) | produce a static library: `StaticLibrary` or `AsmProduceStaticLibrary=true` |
| `undefined symbol: strsignal` in a `libc` `Exe` | the libc stub does not list the function | `<AsmLibcSymbol Include="strsignal" />` |
| `undefined symbol: x` in an `Exe`, or in a `.so` linked with `-z defs` | a misspelled `extern`, a missing source or reference, or a symbol that is `hidden` in another library | fix the name; add the reference; export the symbol |
| `duplicate symbol: helper` (defined twice at `helpers.asm:5`) | an `.asm` file is `%include`d and also assembled as a source | rename it `.inc`, or `<AsmSource Remove="helpers.asm" />` |
| `cannot open <project>/obj/Debug/<tfm>/asm/obj/x.o: no such file or directory` | known bug: a `ProjectReference` to an `OutputType=Objects` project passes relative object paths | make the referenced project a `StaticLibrary` |

## Builds that pass but behave wrongly

| Symptom | Cause | Fix |
|---|---|---|
| editing a shared `.inc` outside the project does not rebuild | only `AsmInclude` items and package or reference folders are dependencies | add `<AsmInclude Include="../common/include/**/*.inc" />` |
| a packaged library's consumers can't find its include file | the producer had no `include/` folder, so nothing was packed | move public files to `include/`, or declare `AsmPublicInclude` items |
| `DEBUG` code is missing in Debug builds | `DisableImplicitConfigurationDefines=true` | remove it, or add `DEBUG` to `DefineConstants` yourself |
| the `.so` exports a dependency's functions | a linked `.a`'s globals are exported | `<LinkerArg Include="--exclude-libs ALL" />`, an `ExportsFile`, or `hidden` in the dependency |
| `RUNPATH` is empty although `-rpath $ORIGIN` is set | Linux and macOS build machines run the link command through `/bin/sh` | per-OS quoting; see [Runtimes and linking](runtimes-and-linking.md) |
| outputs differ between two machines | NASM embeds each source's absolute path | the same checkout path, or `-s` in Release |
| a typo in an `extern` in a `.so` links fine and fails only at load time with `undefined symbol` | shared libraries may have undefined symbols | `<LinkerArg Include="-z defs" />` |

## Running

| Symptom | Cause | Fix |
|---|---|---|
| `Exec format error`, or `DllNotFoundException` on Windows or macOS | the outputs are Linux x86-64 ELF | run on Linux: CI, WSL, or `docker run … mcr.microsoft.com/dotnet/runtime:10.0` |
| `Permission denied` running an executable in a container | files built on Windows have no execute bit | `chmod +x` first |
| `version 'GLIBC_2.34' not found`, or `libpthread`/`libdl` symbols missing | a `libc` program on a system with glibc older than 2.34 | a newer distribution (Ubuntu 22.04+, Debian 12+, RHEL 9+), or `freestanding` |
| `not found` for `/lib64/ld-linux-x86-64.so.2` on Alpine | musl has no glibc loader | `freestanding`, or a glibc base image |
| a crash (`SIGSEGV`) inside `printf` or another libc function | stack not 16-byte aligned at the call (`movaps` faults), or `al` not set for a variadic call | push one register (or `sub rsp, 8 + 16k`) before calling; `xor eax, eax` before variadic calls |
| a crash after returning from your function into C# or Python | a callee-saved register (`rbx`, `rbp`, `r12`–`r15`) was clobbered | save and restore it |
| wrong results only for negative `int` arguments | the code read `rdi` (64-bit) for a 32-bit `int` | `movsxd rax, edi` |
