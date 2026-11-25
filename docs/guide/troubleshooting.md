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
| `MSB4236: The SDK 'AsmFoundation.Sdk' specified could not be found` | no feed has that version, or `global.json` lacks `msbuild-sdks` | add `{"msbuild-sdks": {"AsmFoundation.Sdk": "0.3.0"}}`, or name the version on the project's first line (`<Project Sdk="AsmFoundation.Sdk/0.3.0">`); add the feed to `nuget.config` (folder feeds work) |
| `The NuGetSdkResolver did not resolve this SDK because there was no version specified in the project or global.json` with `Sdk="AsmFoundation.Sdk/*"` (or `/0.*`, `/latest`, or `"*"` in `global.json`) | an MSBuild SDK takes an exact version only: the resolver has no floating versions (measured) | an exact version: `0.3.0` |
| `ASM0003: TargetFramework 'netstandard2.0' is not an AsmSdk target framework` | a `TargetFramework` other than `freestanding` or `libc`, such as 0.1.0's hidden `netstandard2.0` | `freestanding` or `libc`, or `TargetFrameworks=freestanding;libc` |
| `ASM0100` to `ASM0107: … was removed in AsmFoundation.Sdk 0.2.0`, then `ASM0108` | a 0.1.0 setting (`AsmLibraryName`, `AsmRuntime`, `AsmDefineConstants`, `AsmDefine`, `AsmDebugInfo`, `AsmEntryPoint`, `AsmLinkFlags`, `AsmOutputKind`) | the replacement each error names; the [changelog](changelog.md) has the whole table |
| `ASM0109: AsmLinkLibrary items were replaced after AsmFoundation.Sdk 0.3.0 by NativeLibrary items` (not released yet) | the project lists `AsmLinkLibrary` items | `<NativeLibrary Include="…" />`, NativeAOT's item name; the error shows the line |
| `ASM0109: The package X was packed by AsmFoundation.Sdk 0.3.0 or older` (not released yet) | the package's props add `AsmLinkLibrary` items, which this SDK does not link | pack the package again with this SDK, and reference the new version |
| `Ambiguous project name 'x'` (restore) | two projects of one restore graph share an `AssemblyName`, for example a static and a shared build of the same library (`libgrpcasm.a`, `libgrpcasm.so`). `PackageId` defaults to `AssemblyName`, as in .NET, and NuGet names projects by it. 0.1.0's `AsmLibraryName` did not touch it, so this appears when migrating. | give each project its own `<PackageId>`; the file names still follow `AssemblyName` |
| `NU1201: Project X is not compatible with netstandard1.0 (.NETStandard,Version=v1.0)` | a `freestanding` project references a `libc`-only project: freestanding code cannot link C library code | make the reference `freestanding` (or `TargetFrameworks=freestanding;libc`), or make this project `libc` |
| `NU1202: Package X is not compatible with netstandard1.0` | the same with a `libc`-only package | the same, in the package's project |
| `ASM0006: There is no bundled toolchain for osx-x64 build machines` (or `linux-musl-x64`) | LLVM publishes no release binaries for Intel Macs, and its Linux binaries need glibc | `<AsmToolchain>System</AsmToolchain>` with Homebrew's or Alpine's `nasm`, `llvm` and `lld`, or `AsmHostRid` set to a toolchain that runs there; see [The toolchain](../toolchain/index.md) |
| `NU1101: Unable to find package AsmFoundation.Toolchain.<rid>` | the feed holding it is not mapped, or a toolchain version before the build machine had one (0.1.0 had no `win-arm64` or `linux-arm64`) | check the package source mapping; `AsmToolchainVersion` 0.2.0 or later |
| `NU1101: Unable to find package <your package>` while the `.nupkg` is in a folder feed | package source mapping, possibly from a parent `nuget.config`, routes the id elsewhere | map the id's pattern to that feed; start the mapping with `<clear />` |
| `ASM0001: The bundled toolchain was not found in '…'` | built with `--no-restore` on a clean cache, cleaned caches, or a different `NUGET_PACKAGES` at build time | `dotnet restore`, then build; keep `NUGET_PACKAGES` the same |
| `ASM0002: AsmToolchain must be 'Bundled' or 'System'` | typo | fix the value |
| `MSB4025 … ProjectType '' not found` on `dotnet build x.slnx` | an `.asmproj` entry without a type | `<Project Path="…asmproj" Type="13B669BE-BB05-4DDF-9536-439F39A36129" />` |
| `… has an unknown project type and cannot be added to the solution file` from `dotnet sln add` | the .NET CLI does not know the `.asmproj` extension | add the entry by hand (`.slnx`: one line with the type GUID), or use `new_asmproj.py --sln`. Not released yet: the SDK names the type (`DefaultProjectTypeGuid`), and `dotnet sln add` works |
| `Project … cannot be added due to incompatible targeted frameworks between the two projects` from `dotnet add reference` (or `dotnet reference add`) to an `.asmproj` | the .NET CLI parses the referenced project's `TargetFramework` text, and does not know NuGet's aliases (`freestanding`, `libc`); NuGet and the build accept them ([dotnet/sdk#57317](https://github.com/dotnet/sdk/issues/57317)) | write the line in the `.csproj`: `<ProjectReference Include="../Native/Native.asmproj" />` |
| a classic `.sln` build reports 0 errors but never builds the asm project | the `.sln` lists the project but has no `ProjectConfigurationPlatforms` lines for it | add `{guid}.Debug\|Any CPU.ActiveCfg` / `.Build.0` lines (and Release), or move to `.slnx` |
| a changed SDK has no effect while you develop the SDK itself | MSBuild nodes cached the old files | build with `-nodeReuse:false`, and use a fresh `NUGET_PACKAGES` |

## Runtime identifiers

See [Platforms and runtime identifiers](platforms.md) for how a project chooses its RIDs.

| Message | Cause | Fix |
|---|---|---|
| `ASM0010: This project names no runtime identifier to build` | no `RuntimeIdentifiers` in the project, no referencing project asking for one, no `-r`. Not released yet: only on a build machine that is none of the eight RIDs; elsewhere the project builds for the machine's RID | list the RIDs the code supports: `<RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>`; or name them on the `ProjectReference` (`RuntimeIdentifiers="…"`); or build with `-r <rid>` |
| `warning ASM0013: X lists no RuntimeIdentifiers, so this pack carries it for win-x64 alone` (or `this portable publish of App carries its library for the build machine's platform alone`), not released yet | a project without `RuntimeIdentifiers` builds for the build machine's RID; a pack or a portable publish leaves the machine with that one | list the platforms to ship in the asm project, or on the `ProjectReference`; publish with `-r <rid>`; or `<NoWarn>ASM0013</NoWarn>` when one platform is the intent |
| `ASM0011: AsmSdk does not build for linux-x86` | a RID outside the seven: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `linux-musl-x64`, `osx-x64`, `osx-arm64` | one of those |
| `ASM0012: osx-arm64 was asked for (by a referencing project or -r), but this project's RuntimeIdentifiers are …` | a referencing project, a `ProjectReference`'s `RuntimeIdentifiers`, or `-r` asks for a RID the project does not list | add it to the project's list if the code supports it; otherwise build the referencing project for a listed RID |
| `ASM0040: X.asmproj builds no runtime identifier that linux-arm64 can use. It builds …; linux-arm64 can use …` | a .NET project built for one RID references an asm project none of whose RIDs serves it, by the .NET RID graph | add a serving RID to the asm project (the message lists them), or build the .NET project for another RID. Not released yet: when the app copes without the library, `<OptionalOnUnlistedRuntimes>true</OptionalOnUnlistedRuntimes>` in the asm project makes this the warning `ASM0049` |
| `warning ASM0049: X.asmproj builds no runtime identifier that linux-arm64 can use (it builds …), and it is optional on the RIDs it does not list` (not released yet) | the asm project sets `OptionalOnUnlistedRuntimes=true`, and this RID-specific build goes on without it | nothing, if the app handles `DllNotFoundException` for it; otherwise add a serving RID to the asm project |
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
| `ASM0023: No source of this project builds browser-wasm. WebAssembly is LLVM's WebAssembly assembly in .s files qualified for it` (not released yet) | a wasm RID in a project with no source qualified for WebAssembly: unqualified `.s` files are ARM64 code | add `name.wasm.s` (both wasm RIDs), `name.browser.s` or `name.wasi.s`, or remove the RID |
| `ASM0021: x.wasm.asm is a NASM file (.asm) qualified for 'wasm'` (not released yet) | a `.asm` file named for WebAssembly | write WebAssembly in a `.s` file (`x.wasm.s`) |
| `ASM0025: x.c cannot be compiled by an .asmproj` (not released yet) | a `Compile` item that is not `.asm` or `.s` | make it a `None` item; build C or C# in a project of its own |
| `ASM0110`, `ASM0111`, `ASM0112: AsmSource items were replaced after AsmFoundation.Sdk 0.3.0 by Compile items` (and `AsmInclude` by `None`, `EnableDefaultAsmItems` by `EnableDefaultCompileItems`), not released yet | the project names a 0.3.0 item or switch | the line the error shows |
| `ASM0024: Duplicate AsmSource items: x.asm` | a source the default `**/*.asm;**/*.s` glob includes is listed again | remove the explicit item, or `EnableDefaultAsmItems=false` and list every source |
| `ASM0004: x.s is GNU-syntax assembly for llvm-mc, which AsmFoundation.Toolchain.<rid> 0.1.0 does not include` | a toolchain from before `llvm-mc` (also for an ARM64 `libc` build, whose C library stubs are GNU syntax) | `AsmToolchainVersion` 0.2.0 or later |
| `error: unknown section name .note.GNU-stack` (macOS) | the Linux-only note in a source built for macOS | wrap it in `%ifdef LINUX` … `%endif` |
| `error: unrecognised symbol type 'function'` (macOS) | `global f:function`, which is ELF's | `FUNCTION f` from `asmsdk.inc`, or `global f` in a macOS file |
| `error: symbol '..plt' not defined` or `'..got' not defined` (Windows, macOS) | `wrt ..plt` and `wrt ..got` are ELF-only | `call f PLT` (empty off Linux); `wrt ..gotpcrel` on macOS |
| `error: Mach-O 64-bit format does not support 32-bit absolute addresses` | a memory operand without `default rel` | `default rel` |
| `error: unrecognised symbol type 'hidden'` or `'private_extern'` | each format's visibility keyword in another format's file | `HIDDEN_FUNCTION f` from `asmsdk.inc` |

## WebAssembly (not released yet)

| Message | Cause | Fix |
|---|---|---|
| `ASM0019: x.wasm.s: llvm-mc's WebAssembly object writer stopped (LLVM ERROR: LIMIT: absolute addressing not supported!)` | an assembler constant (`.set`, `.equ`, `=`): llvm-mc's wasm objects hold no constant symbols, and its writer crashes on one | write the number itself, or a macro that expands to `i32.const 4096` |
| `ASM0019: … (LLVM ERROR: data symbols must have a size set with .size: table)` | a data label without a size | `ENDDATA table` after its last byte (`.size table, . - table`); a `.L` label needs none |
| `ASM0019: … (LLVM ERROR: section already has a defining function: .data)` | code after `DATA` (or `RODATA`, `BSS`) and a bare `.text`, which llvm-mc's wasm parser ignores, so the function landed in the data section | start each function with `FUNCTION`, which switches to its own section |
| `ASM0018: The define 'LEVEL=3' has a value, and a WebAssembly source cannot receive one` | `DefineConstants` with a value, which a WebAssembly source receives as a label | define the name alone and write the value in the source |
| `ASM0017: X is a program (OutputType Exe), and browser-wasm has none` | an `Exe` for the browser | a `Library` (a module with exports), or the program for `wasi-wasm` |
| `ASM0016: X links a WebAssembly module for wasi-wasm with TargetFramework libc` | the bundled wasm-ld links no C library | `freestanding` (calling WASI or JavaScript imports), or a `StaticLibrary` for a .NET WebAssembly app, whose build brings its C library |
| `ASM0030: x.map exports 'f_*', a pattern; WebAssembly modules export exact names only` | a pattern in a wasm module's `ExportsFile` | list each name |
| `error: symbol f: missing .functype` | a call to a function whose type llvm-mc has not seen: defined in another file, or further down | `EXTERN_FUNCTION f, (i32) -> (i32)` before the call |
| `error: Expected EOL, instead got: ,` on `.int32 1, 2` | the wasm parser's `.int8` to `.int64` take one value | `.int 1, 2` (or `.byte`, `.short`, `.quad`) |
| `error: unknown directive` on `.bss` or `.rodata` | llvm-mc's wasm parser knows neither bare directive | `BSS` and `RODATA` from `asmsdk-wasm.inc`, or `.section .bss,"",@` |
| `error: instruction requires: …` | an instruction of a feature the SDK does not enable (the SDK enables SIMD128 and tail calls) | `<AsmLlvmMcFlags>-mattr=+<feature></AsmLlvmMcFlags>` |
| `undefined symbol: f` from wasm-ld | a function nobody defines; WebAssembly links no host function implicitly | `IMPORT_FUNCTION env, f, (…) -> (…)` for a host function, or add the source or reference that defines it |
| `return_call* requires tail calls [--enable-tail-call]` from wasm-opt, in a .NET browser-wasm app's Release build | a hand-written `return_call` in code linked into .NET: .NET 10's wasm-opt validates without tail calls | `TAILCALL` (a call and a return) |
| `Pointer not aligned to 4` from wasmtime | a WASI call got a misaligned pointer (an iovec, a result address); llvm-mc aligns no data by itself | `.p2align 2` before the data |
| `DllNotFoundException` in a .NET WebAssembly app | the `[DllImport]` name is not the asm project's `AssemblyName`, or the asm project lists no RID for the app | use the `AssemblyName`; add `browser-wasm` or `wasi-wasm` to its `RuntimeIdentifiers` (ASM0040 says so for a project reference) |
| `ASM0054: App links WasmLibrary into its browser-wasm runtime, which only .NET's native WebAssembly build does, and this .NET SDK (11.0.100) has none for net10.0` | the workload of the app's .NET version is missing, so .NET would build the app as plain IL, without the asm code. Measured: SDK 11.0.100 with Visual Studio's `wasm-tools`, which serves `net11.0`, built a `net10.0` app with no `AppBundle` and no warning | what the message names: `dotnet workload install wasm-tools` (`wasi-experimental` for WASI) on the SDK of the app's own .NET. On a later SDK it names the fix of the app's shape. Measured with SDK 11.0.100: an app made with `Microsoft.NET.Sdk.WebAssembly` built and ran with `wasm-tools-net10`, so that workload is the fix; one made with `Microsoft.NET.Sdk` and `RuntimeIdentifier` `browser-wasm` did not build with it (.NET's own `wasmconsole` template neither), and a WASI build serves only `net11.0`, so for those the fix is the app's own SDK in `global.json` |
| `warning ASM0055: App sets RunAOTCompilation, but .NET compiles a WebAssembly app ahead of time only when it publishes it: this build interprets the C#` | a build (not a publish) of a browser app that links asm code, with `RunAOTCompilation=true`: .NET compiles ahead of time only in a publish | time a `dotnet publish`; when the property is there for the publishes (a Blazor setup), `<NoWarn>$(NoWarn);ASM0055</NoWarn>` |
| C# runs 10 or more times slower in Chrome or Firefox than in Node, in a .NET browser app's build | .NET's debugger: a build's boot configuration says `debugLevel: -1`, and the runtime then starts the debugger in Chromium and Firefox, with the interpreter unoptimized | AsmSdk sets `WasmDebugLevel` 0 in a Release build of an app that links asm code; a project that sets `-1` itself, or a Debug build, keeps the debugger. Time a Release build or a publish |
| `error: PROTO and the function macros go between functions: f has no ENDFUNCTION yet`, with `note: while in macro instantiation` at the line | a `PROTO` line (or the next `FUNCTION`) inside a function's body. In a wasm object the record would take the rest of the body out of the function's section, and the function would lose its `end` | move the `PROTO` line above its `FUNCTION`, and close each function with `ENDFUNCTION name` |
| `ASM0132: PROTO f lowers to the WebAssembly type (i64) -> (i64) (…), but its FUNCTION in x.wasm.s declares (i32) -> (i32)` | the C prototype and the wasm type disagree: the C# binding would pass what the code does not read | make them agree: pointers, `bool` and the integers of 32 bits or fewer are `i32`, `int64_t` and `uint64_t` `i64`, `float` `f32`, `double` `f64` |
| `ASM0133: The RIDs declare f differently: …; browser-wasm: not declared` | a library whose native RIDs have a `PROTO` line its wasm source lacks (or the other way round): the bindings are one baseline for every RID | the same `PROTO` lines in every RID's source, the `.wasm.s` included |
| `error: type mismatch, expected [i32] but got [i32, i32]` at `ENDFUNCTION` | an untyped `select` in the function: llvm-mc 23.1.5's type checker refuses it | an `if i32` … `else` … `end_if` that leaves the value |
| `error: type mismatch, expected [] but got [v128]` at an `end_loop` or `end_block` | the body leaves a value on the stack, often a `local.tee` whose value nothing takes | `local.set` instead |
| `error: Cannot parse real: 0.42` on `f32.const` (or `f64.const`) | llvm-mc's WebAssembly parser takes a decimal only when it is short and exact (`1.0`, `0.5`, `256.0`); an integer (`f32.const 1`) or any other decimal is refused, even an exact long one | the hex float, as llvm-mc prints it: `f32.const 0x1.ae147ap-2` is 0.42 (Python: `float.hex` of the value rounded to f32) |
| `ASM0019: … (LLVM ERROR: .set redefinition is not supported)` | a `.set` of a name already set, which the wasm target refuses even for a `.L` name | a label defined once, or a macro that is purged and defined again |
| `Error: no perHostConfigs found` from `dotnet run` of a .NET browser app | WasmAppHost (the program `dotnet run` starts) found no host to run the bundle with | a `runtimeconfig.template.json` beside the project with `"wasmHostProperties": { "perHostConfig": [ { "name": "node", "js-path": "main.mjs", "host": "nodejs" } ] }`, as the `asmwasmapp` template has |
| `MONO_WASM: Error loading symbol file dotnet.native.js.symbols: {}` when a .NET browser app starts in Node | the runtime's loader could not read the symbol map it uses to name native frames in stack traces, though the bundle has the file (measured with Node 22.14 and .NET 10.0.15) | nothing: the app runs, and only a native stack trace would lose its names |

## Assembling (NASM)

| Message | Cause | Fix |
|---|---|---|
| `ASM0007: a.asm: the assembler exited with code 1; its object file was deleted` | any assembler error; the assembler's line comes just before | fix the error it names. The deleted object makes the next build assemble the source again |
| `warning: implicit DEFAULT ABS is deprecated [-w+implicit-abs-deprecated]` (an error with `TreatWarningsAsErrors`) | a memory operand in a file without `default rel` | add `default rel` at the top of every source. Not released yet: `ImplicitUsings` (on by default) puts it in front of every source; this warning then means `<ImplicitUsings>disable</ImplicitUsings>` |
| `error : instruction expected, found `FUNCTION …'` (and `label `RETURN' alone on a line without a colon`) | `asmsdk.inc` is not included: a source without `%include "asmsdk.inc"`, or (not released yet) a project with `<ImplicitUsings>disable</ImplicitUsings>` | `%include "asmsdk.inc"` at the top, or leave `ImplicitUsings` on |
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
| `undefined symbol: main` (or a static library's function, or an `ExportsFile` export) on Windows only, although a NASM source defines it | the source has no `section` directive before the label: NASM's COFF output writes a label defined before the first section directive as an undefined symbol, though its code lands in `.text` (measured with NASM 2.16.03, 3.01 and 3.02; ELF and Mach-O are not affected). A static library archives such a symbol without complaint; the link of whatever uses it fails | `section .text` before the first label. Not released yet: `ImplicitUsings` (on by default) ends with `section .text`, and `asmsdk.inc`'s function macros start with `__SECT__`, so a source that names no section links on every OS, with `ImplicitUsings` off too |
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
| the C# project gets the `[AsmLibrary]` types, and its build loads the bindings generator, though no code uses the attribute (not released yet) | the switch is a word search of the project's C# files: `AsmLibrary` in a comment, a string or another name turns the generator on. It writes the same baseline | nothing, or reword the text; to check, the switch reads `Compile` items only |
| `error CS0101: The namespace '<global namespace>' already contains a definition for 'Apiproto'` in a C# project with asm references (not released yet, measured) | a type of the project is named like an asm library's baseline namespace (the library's name in PascalCase) | name the baseline's namespace: `<AsmBindings Include="apiproto" Namespace="Native.Apiproto" />`, or rename the type |
| `error CS8936: Feature 'ref fields' is not available in C# 10.0` in `obj/.../AsmBindings/*.g.cs` or a package's `AsmBindings/*.cs` (not released yet, measured with `LangVersion` 10) | the project sets a `LangVersion` below 11; the string wrappers' `scoped` locals need C# 11 (the generator would warn `ASM0147` and write nothing) | `<LangVersion>11</LangVersion>` or later, or `AsmGenerateBindings=false` and imports of your own |

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

## Programs: run, publish, deliver and pack

> [!NOTE]
> **Not released yet:** these come from the main branch (see [Programs](programs.md)). With
> AsmFoundation.Sdk 0.3.0, `dotnet run` of a program says "The current OutputType is
> 'Library'" and `dotnet publish` fails with `MSB3030`.

| Message | Cause | Fix |
|---|---|---|
| `ASM0090: X builds a shared library (OutputType Library), and dotnet run starts programs` | `dotnet run` of a library, static library or object files project | run a program; a library runs inside the program that loads it |
| `ASM0091: This machine (win-x64) cannot run any build of X (osx-arm64): …` | no build of the program runs here, natively, emulated, through WSL or in Docker; the message lists what it tried | build a RID this machine runs (`dotnet run -r <rid>` with the RID in `RuntimeIdentifiers`), install WSL with a distribution, or start Docker |
| `ASM0092: X has no built program to run` | `dotnet run --no-build` before any build | build first |
| `ASM0093: AsmRunIn 'x' is not a way to run a program` | a typo | `Auto`, `Host`, `Wsl` or `Docker` |
| `exec format error` from `dotnet run` in Docker | a build for another CPU on a Linux machine without QEMU in `binfmt_misc` | install it (`docker run --privileged --rm tonistiigi/binfmt --install all`), or run the build of the machine's CPU |
| `error while loading shared libraries: libcalc.so` | a program run from somewhere other than its build or publish folder, without its library next to it; or a program built by AsmSdk 0.3.0, which set no run path | copy the whole folder; rebuild with the main branch |
| `ASM0047: calc.dll would be copied to lib/, but a Windows program finds the DLLs it links only in its own folder` | `BinariesCopyPath` other than `AppFolder` for a Windows RID | set the value for the other RIDs only (a condition on `$(RuntimeIdentifier)`) |
| `ASM0048` (warning): `… links calc.dll.lib, the import library of calc.dll, but the package … does not say where calc.dll is` | a package packed by AsmSdk 0.3.0, whose props do not name the DLL | repack the package with the main branch, or copy the DLL next to the program |
| `ASM0051: A packed program keeps the libraries and programs it needs next to it, and these are in subfolders` | `dotnet pack` of a program (or a .NET library that carries one) whose libraries are in a `BinariesCopyPath` folder: NuGet copies `runtimes/` files flat into an app built for one RID | keep them next to the program (`AppFolder`, the default) |
| `ASM0053: PackAsTool makes a program a .NET tool, and X builds a SharedLibrary` | `PackAsTool` on something that is not a program, or with several target frameworks | `OutputType=Exe`, one `TargetFramework` |
| a .NET app does not find the program it references | it looked only in its own folder, and a portable build keeps the program in `runtimes/<rid>/native/` | search `NATIVE_DLL_SEARCH_DIRECTORIES`, then the app folder (the code is in [Programs](programs.md#a-net-app-starts-it)) |
