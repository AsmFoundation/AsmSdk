# Troubleshooting

Find the message, then apply the fix. Unless a row says otherwise, every message below
was reproduced with AsmFoundation.Sdk 0.1.0, NASM 3.02 and `ld.lld` 23.1.5, and is
written with the current property names. The `ASM0003`, `ASM0100` to `ASM0108` and
`Ambiguous project name` rows, and the target framework rows, come from 0.2.0. The other
`ASM00xx` codes, and the Windows and macOS rows, come from 0.3.0 and its lld-link, ld64.lld
and llvm-mc.

**First, get the real message.** A failed assembler run is reported as **ASM0007**, naming the
source, right after the assembler's own line (`…/a.asm:5: error: …`). `-v:q` hides that line.
Other failed steps surface as:

```text
<sdk>/targets/AsmFoundation.Build.targets(<line>,5): error MSB3073: The command ""…/lld.exe" -flavor link …" exited with code 1.
```

with the tool's own message just above it.

- Add `<AsmFlags>-Xvc</AsmFlags>` (best in `Directory.Build.props`). NASM's messages then
  become MSBuild errors and warnings with file and line:
  `a.asm(5): error : instruction expected, found 'movx eax'`.
- `lld` errors are already recognized: `lld : error : …`.
- The command in `MSB3073` tells you which step failed:

  | The command runs | Step |
  |---|---|
  | `lld -flavor gnu`, `link` or `darwin` with `-o`/`-out:` the output | linking for Linux, Windows or macOS |
  | `llvm-ar rcsD` | archiving a static library |
  | `lld -flavor link -lib -def:` | writing an import library (kernel32, the UCRT) |
  | `nasm` or `llvm-mc` on `libc-stub`, `nonshared`, `winstdio` or `crt1` | building the C library stand-ins or the startup code |

## Restore and SDK resolution

| Message | Cause | Fix |
|---|---|---|
| `MSB4236: The SDK 'AsmFoundation.Sdk' specified could not be found` | no feed has that version, or `global.json` lacks `msbuild-sdks` | add `{"msbuild-sdks": {"AsmFoundation.Sdk": "0.3.0"}}`; add the feed to `nuget.config` (folder feeds work) |
| `ASM0003: TargetFramework 'netstandard2.0' is not an AsmSdk target framework` | a `TargetFramework` other than `freestanding` or `libc`, such as 0.1.0's hidden `netstandard2.0` | `freestanding` or `libc`, or `TargetFrameworks=freestanding;libc` |
| `ASM0100` to `ASM0107: … was removed in AsmFoundation.Sdk 0.2.0`, then `ASM0108` | a 0.1.0 setting (`AsmLibraryName`, `AsmRuntime`, `AsmDefineConstants`, `AsmDefine`, `AsmDebugInfo`, `AsmEntryPoint`, `AsmLinkFlags`, `AsmOutputKind`) | the replacement each error names; the [changelog](changelog.md) has the whole table |
| `Ambiguous project name 'x'` (restore) | two projects of one restore graph share an `AssemblyName`, for example a static and a shared build of the same library (`libgrpcasm.a`, `libgrpcasm.so`). `PackageId` defaults to `AssemblyName`, as in .NET, and NuGet names projects by it. 0.1.0's `AsmLibraryName` did not touch it, so this appears when migrating. | give each project its own `<PackageId>`; the file names still follow `AssemblyName` |
| `NU1201: Project X is not compatible with netstandard1.0 (.NETStandard,Version=v1.0)` | a `freestanding` project references a `libc`-only project: freestanding code cannot link C library code | make the reference `freestanding` (or `TargetFrameworks=freestanding;libc`), or make this project `libc` |
| `NU1202: Package X is not compatible with netstandard1.0` | the same with a `libc`-only package | the same, in the package's project |
| `ASM0006: There is no bundled toolchain for osx-x64 build machines` (or `linux-musl-x64`) | LLVM publishes no release binaries for Intel Macs, and its Linux binaries need glibc | `<AsmToolchain>System</AsmToolchain>` with Homebrew's or Alpine's `nasm`, `llvm` and `lld`, or `AsmHostRid` set to a toolchain that runs there; see [The toolchain](../toolchain/index.md) |
| `NU1101: Unable to find package AsmFoundation.Toolchain.<rid>` | the feed holding it is not mapped, or a toolchain version before the build machine had one (0.1.0 had no `win-arm64` or `linux-arm64`) | check the package source mapping; `AsmToolchainVersion` 0.2.0 or later |
| `NU1101: Unable to find package <your package>` while the `.nupkg` is in a folder feed | package source mapping, possibly from a parent `nuget.config`, routes the id elsewhere | map the id's pattern to that feed; start the mapping with `<clear />` |
| `ASM0001: The bundled toolchain was not found in '…'` | built with `--no-restore` on a clean cache, cleaned caches, or a different `NUGET_PACKAGES` at build time | `dotnet restore`, then build; keep `NUGET_PACKAGES` the same |
| `ASM0002: AsmToolchain must be 'Bundled' or 'System'` | typo | fix the value |
| `MSB4025 … ProjectType '' not found` on `dotnet build x.slnx` | an `.asmproj` entry without a type | `<Project Path="…asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />` |
| `… has an unknown project type and cannot be added to the solution file` from `dotnet sln add` | the .NET CLI does not know the `.asmproj` extension | add the entry by hand (`.slnx`: one line with the type GUID), or use `new_asmproj.py --sln` |
| a classic `.sln` build reports 0 errors but never builds the asm project | the `.sln` lists the project but has no `ProjectConfigurationPlatforms` lines for it | add `{guid}.Debug\|Any CPU.ActiveCfg` / `.Build.0` lines (and Release), or move to `.slnx` |
| a changed SDK has no effect while you develop the SDK itself | MSBuild nodes cached the old files | build with `-nodeReuse:false`, and use a fresh `NUGET_PACKAGES` |

## Runtime identifiers

See [Platforms and runtime identifiers](platforms.md) for how a project chooses its RIDs.

| Message | Cause | Fix |
|---|---|---|
| `ASM0010: This project names no runtime identifier to build` | no `RuntimeIdentifiers` in the project, no referencing project asking for one, no `-r` | list the RIDs the code supports: `<RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>`; or name them on the `ProjectReference` (`RuntimeIdentifiers="…"`); or build with `-r <rid>` |
| `ASM0011: AsmSdk does not build for linux-x86` | a RID outside the seven: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64` | one of those |
| `ASM0012: osx-arm64 was asked for (by a referencing project or -r), but this project's RuntimeIdentifiers are …` | a referencing project, a `ProjectReference`'s `RuntimeIdentifiers`, or `-r` asks for a RID the project does not list | add it to the project's list if the code supports it; otherwise build the referencing project for a listed RID |
| `ASM0040: X.asmproj builds no runtime identifier that linux-arm64 can use. It builds …; linux-arm64 can use …` | a .NET project built for one RID references an asm project none of whose RIDs serves it, by the .NET RID graph | add a serving RID to the asm project (the message lists them), or build the .NET project for another RID |
| `ASM0041: Cannot read the runtime identifier graph` | the .NET SDK's `RuntimeIdentifierGraphPath` points to a missing or unreadable file | repair the .NET SDK installation |
| `ASM0042: Cannot read … deps.json` | the `.deps.json` the .NET SDK wrote is missing or malformed when the SDK adds the asm libraries | rebuild; report it if it persists |
| `ASM0043: BinariesCopyPath '../out' on the reference to X.asmproj cannot be used: it leaves the output folder` | a `BinariesCopyPath` that is absolute, has `..`, a character no OS takes in a file name, or a placeholder other than `{rid}` | `Auto`, `Runtimes`, `AppFolder`, `RidFolders`, or a folder inside the output such as `native/{rid}` |
| `ASM0044: X.asmproj's win-x64 build of x.dll and X.asmproj's win-arm64 build of x.dll would both be copied to native/x.dll` | a folder without `{rid}` in a portable build of several RIDs of one OS; or two libraries with one file name | add `{rid}` (the message suggests the folder), or narrow the reference's `RuntimeIdentifiers` to one RID per OS |
| `ASM0046: A deploys X.asmproj with BinariesCopyPath 'native', B with 'RidFolders'` (warning) | two .NET libraries of the app chose differently for one asm library | set `BinariesCopyPath` in the app: on a reference to the asm project, or as the property |
| `ASM0060: The package X 1.0.0 has no build for osx-arm64` | an asm project references a package packed for other RIDs | build for a RID the message lists, or use a version packed for yours |
| `DllNotFoundException` in a .NET app on one platform only | the asm project lists no RID for that platform, or the app was built for another RID | add the RID to the asm project; check `runtimes/<rid>/native/` in the output (or the `BinariesCopyPath` folder) |
| `DllNotFoundException` after moving the libraries with `BinariesCopyPath` | something loads them without the host's `deps.json` and without the generated loader: a NativeAOT program of a VB or F# project, a project with `AsmGenerateNativeLibraryLoader=false`, or files copied by hand | check the app's `deps.json` for the library with its `localPath`; for NativeAOT keep the default layout (the app folder), or load the library yourself (`NativeLibrary.SetDllImportResolver`) |

## Sources per platform

| Message | Cause | Fix |
|---|---|---|
| `ASM0023: No source of this project builds linux-arm64` | an ARM64 RID in a project of `.asm` files only: NASM assembles x86-64 alone | add a `.s` file (GNU syntax) for ARM64, or remove the ARM64 RIDs |
| `ASM0021: x.arm64.asm is a NASM file (.asm) qualified for 'arm64'` | a `.asm` file named for ARM64 targets only | write ARM64 code in a `.s` file |
| `ASM0020: For win-x64, x.win.asm and x.win.s apply equally` | two files of one name, equally specific for a RID | keep one, or qualify one more narrowly (`x.win-x64.asm`) |
| `ASM0024: Duplicate AsmSource items: x.asm` | a source the default `**/*.asm;**/*.s` glob includes is listed again | remove the explicit item, or `EnableDefaultAsmItems=false` and list every source |
| `ASM0004: x.s is GNU-syntax assembly for llvm-mc, which AsmFoundation.Toolchain.<rid> 0.1.0 does not include` | a toolchain from before `llvm-mc` (also for an ARM64 `libc` build, whose C library stubs are GNU syntax) | `AsmToolchainVersion` 0.2.0 or later |
| `error: unknown section name .note.GNU-stack` (macOS) | the Linux-only note in a source built for macOS | wrap it in `%ifdef LINUX` … `%endif` |
| `error: unrecognised symbol type 'function'` (macOS) | `global f:function`, which is ELF's | `FUNCTION f` from `asmsdk.inc`, or `global f` in a macOS file |
| `error: symbol '..plt' not defined` or `'..got' not defined` (Windows, macOS) | `wrt ..plt` and `wrt ..got` are ELF-only | `call f PLT` (empty off Linux); `wrt ..gotpcrel` on macOS |
| `error: Mach-O 64-bit format does not support 32-bit absolute addresses` | a memory operand without `default rel` | `default rel` |
| `error: unrecognised symbol type 'hidden'` or `'private_extern'` | each format's visibility keyword in another format's file | `HIDDEN_FUNCTION f` from `asmsdk.inc` |

## Assembling (NASM)

| Message | Cause | Fix |
|---|---|---|
| `ASM0007: a.asm: the assembler exited with code 1; its object file was deleted` | any assembler error; the assembler's line comes just before | fix the error it names. The deleted object makes the next build assemble the source again |
| `warning: implicit DEFAULT ABS is deprecated [-w+implicit-abs-deprecated]` (an error with `TreatWarningsAsErrors`) | a memory operand in a file without `default rel` | add `default rel` at the top of every source |
| `warning: 32-bit absolute section-crossing relocation [-w+reloc-abs-dword]` | absolute addressing, usually the same root cause. Also given for `dd f wrt ..imagebase` (Windows unwind tables), which is right | `default rel` |
| `error: unable to open include file 'b.inc'` | NASM looks in the project folder and the `-I` folders, never beside the including file | `%include "sub/b.inc"`, or `<AsmIncludeDirectory Include="sub/" />`; for a package or reference, check it ships the file under `include/` |
| `error: label, instruction or prefix expected at start of line, found 'rel'` | a symbol named like a keyword or mnemonic (`rel`, `wait`, `syscall`) | write it as `$rel` in the definition and in every reference |
| `error: DEBUG is not defined` (from your own `%error`) | `DisableImplicitConfigurationDefines=true`, or a Release build (it defines `RELEASE`) | remove the property; test `%ifdef DEBUG` only for Debug-only code |
| a `%define` written by an MSBuild target came out as `Þfine …` | `%de` in an MSBuild string is an escape for byte `0xDE` | write `%25define` |
| `warning: label 'x' alone on a line without a colon might be in error [-w+label-orphan]` | a missing `:` or a typo | add the colon; or `-w-label-orphan` |
| `warning: dword exceeds bounds [-w+number-overflow]` | the constant is too large for its size | fix the size or the constant |
| `nasm: warning: unknown warning name: reloc-rel-dword` (System toolchain) | NASM 2.x does not know NASM 3's warning names | harmless; or install NASM 3 |
| `panic: intra-segment OUT_REL4ADR` | `call f wrt ..plt` (or `call f PLT`) to a function in the same section of the same file | a plain `call f`: the PLT is for other libraries |

## Linking

| Message | Cause | Fix |
|---|---|---|
| `relocation R_X86_64_PC32 cannot be used against symbol 'f'; recompile with -fPIC` (Linux) | a `.so` calls a function of *another library* directly | `call f PLT` (`wrt ..plt`). Within the library, a plain call links, thanks to `-Bsymbolic` |
| `relocation R_X86_64_PC32 cannot be used against symbol 'stdout'` (Linux) | a `.so` reads another library's data directly | `mov rax, [rel stdout wrt ..got]` then `mov rax, [rax]` |
| `relocation R_X86_64_32S cannot be used against local symbol; recompile with -fPIC` (Linux) | absolute 32-bit addressing (no `default rel`) in a `.so` or PIE | `default rel` |
| `relocation R_X86_64_64 cannot be used against local symbol; recompile with -fPIC` (Linux) | `mov rax, label`, or `dq label` in a read-only section, in a `.so` or PIE | `lea rax, [label]`; offset tables in `.rodata`, or `dq` in `.data` / a `write` section |
| `ASM0080: x.dll needs 1 load-time fixup(s) of a 32-bit absolute address (.text+0x4)` (Windows) | a memory operand without `default rel`, or `dd label`: lld-link links it, but a 64-bit image loads above 4 GB, where the address is wrong. The image is deleted | `default rel`; `dq label` in data tables. NASM's `32-bit absolute section-crossing relocation` warning names the line |
| `ASM0081: libx.dylib needs 1 load-time fixup(s) in a read-only segment (rebase at 0x29A in __TEXT)` (macOS) | an absolute address in code or in `.rodata` (`mov rax, label`, `dq label`, a `.quad` in `__text`): a text relocation, which Apple's linker refuses. The image is deleted | `lea rax, [rel label]`; `ADDRESS` in GNU syntax; pointer tables in `.data` |
| `ASM0082: Cannot check the fixups of …` | the linked image could not be read, or is not the format its OS uses | rebuild; report it if it persists |
| `ASM0015: A freestanding executable cannot be built for osx-arm64` | macOS supports no static executables: dyld and libSystem start every program | `<TargetFramework>libc</TargetFramework>` for a macOS program, or build a library |
| `ASM0005: AsmToolchain=System links win-x64 with the command in AsmLinkerCommand, and its default, ld, is GNU ld` | the System toolchain's GNU `ld` links ELF only | `<AsmLinkerCommand>lld-link</AsmLinkerCommand>` (Windows) or `ld64.lld` (macOS) |
| `ASM0005: AsmToolchain=System links linux-arm64 with the command in AsmLinkerCommand, and its default, ld, is this machine's GNU ld, which links x64 ELF files only` | a distribution's GNU `ld` links its own machine's ELF only (without the check, GNU `ld` said `file in wrong format`) | `<AsmLinkerCommand>ld.lld</AsmLinkerCommand>`, which links every architecture, or a cross `ld` such as `aarch64-linux-gnu-ld` |
| `ASM0030: … extern "C++" block` or a pattern on Windows | an `ExportsFile` the Windows and macOS linkers cannot take as names | list the exported names directly; Windows takes exact names only |
| `attempted static link of dynamic object <path>/libx.so` (Linux) | a freestanding `Exe` was given a `.so` (a referenced `Library`, or a package with `AsmPackageLinkKind=Shared`) | produce a static library: `StaticLibrary` or `AsmProduceStaticLibrary=true` |
| `undefined symbol: strsignal` in a `libc` program or a Windows or macOS library | the C library list does not have the function | `<AsmLibcSymbol Include="strsignal" />`; on Windows the name must be a `ucrtbase.dll` export |
| `undefined symbol: x` in an `Exe`, or in a `.so` linked with `-z defs` | a misspelled `extern`, a missing source or reference, or a symbol that is hidden in another library | fix the name; add the reference; export the symbol |
| `duplicate symbol: helper` (defined twice at `helpers.asm:5`) | an `.asm` file is `%include`d and also assembled as a source | rename it `.inc`, or `<AsmSource Remove="helpers.asm" />` |
| `cannot open <project>/obj/Debug/<tfm>/asm/obj/x.o: no such file or directory` (0.1.0, 0.2.0) | a `ProjectReference` to an `OutputType=Objects` project passed object paths relative to the wrong folder | fixed in 0.3.0, which passes absolute paths |

## Builds that pass but behave wrongly

| Symptom | Cause | Fix |
|---|---|---|
| editing a shared `.inc` outside the project does not rebuild | only `AsmInclude` items and package or reference folders are dependencies | add `<AsmInclude Include="../common/include/**/*.inc" />` |
| a packaged library's consumers can't find its include file | the producer had no `include/` folder, so nothing was packed | move public files to `include/`, or declare `AsmPublicInclude` items |
| `DEBUG` code is missing in Debug builds | `DisableImplicitConfigurationDefines=true` | remove it, or add `DEBUG` to `DefineConstants` yourself |
| a shared library exports a dependency's functions | a linked static library's globals (Linux, macOS) or `FUNCTION` directives (Windows) are exported again | `HIDDEN_FUNCTION` or `LOCAL_FUNCTION` in the dependency; an `ExportsFile` (Linux, macOS); `<LinkerArg Include="--exclude-libs ALL" />` on Linux |
| a Windows program has an export table, or a stray `<name>.lib` | before 0.3.0, `FUNCTION main` exported `main` | 0.3.0 defines `ASMSDK_EXECUTABLE`, and programs export nothing without an `ExportsFile` |
| `RUNPATH` is empty although `-rpath $ORIGIN` is set | Linux and macOS build machines run the link command through `/bin/sh` | per-OS quoting; see [Runtimes and linking](runtimes-and-linking.md) |
| outputs differ between two machines | NASM embeds each source's absolute path | the same checkout path, or `-s` in Release |
| a typo in an `extern` in a `.so` links fine and fails only at load time with `undefined symbol` | Linux shared libraries may have undefined symbols | `<LinkerArg Include="-z defs" />` |

## Running

| Symptom | Cause | Fix |
|---|---|---|
| `Exec format error` | a build for another CPU or OS: a Linux build on another OS, or an x64 build on Linux ARM64 | run the build of the machine's own RID; Linux builds from Windows run in WSL or `docker run … mcr.microsoft.com/dotnet/runtime:10.0` |
| `Bad CPU type in executable` (macOS) | an `osx-x64` program on Apple silicon without Rosetta 2 | `softwareupdate --install-rosetta`, or the `osx-arm64` build |
| `Permission denied` running an executable in a container | files built on Windows have no execute bit | `chmod +x` first |
| `version 'GLIBC_2.34' not found`, or `libpthread`/`libdl` symbols missing | a `libc` program on a system with glibc older than 2.34 | a newer distribution (Ubuntu 22.04+, Debian 12+, RHEL 9+), or `freestanding` |
| `not found` for `/lib64/ld-linux-x86-64.so.2` on Alpine | a glibc build (`linux-x64`) on musl | the `linux-musl-x64` build, or `freestanding` |
| a crash (`SIGSEGV`, or `0xC0000005` on Windows) inside `printf` or another C library function | the stack not 16-byte aligned at the call (`movaps` faults); `al` not set for a System V variadic call; no shadow space on Windows | `sub rsp, SHADOW_SPACE + 8` (or a push) before calling; `xor eax, eax` before variadic calls on Linux and macOS |
| a crash after returning from your function into C# or Python | a callee-saved register was clobbered: `rbx`, `rbp`, `r12`–`r15`, and on Windows also `rdi`, `rsi`, `xmm6`–`xmm15` | list them in `FUNCTION`, which saves those this OS expects back |
| wrong results only for negative `int` arguments | the code read the 64-bit register for a 32-bit `int` | `movsxd rax, ARG1d` |
