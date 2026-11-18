# Samples

The [`samples/`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples) folder of the
AsmSdk repository covers every kind of project. CI builds and checks all of them on Windows,
Linux and macOS.

| Sample | Shows |
|---|---|
| [`Hello.Library`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Library) | a shared library (`libhello.so`) with a public `include/` file, built for both target frameworks (`TargetFrameworks=freestanding;libc`): one source, `%ifdef LIBC` |
| [`Hello.Static`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Static) | a static library (`libhellostatic.a`) with public includes, packed as the NuGet package `Hello.Static` 1.0.0 |
| [`Hello.App`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.App) | a freestanding executable that links `Hello.Static` through a `ProjectReference` |
| [`Hello.Libc`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Libc) | a `main()` program on glibc that calls `printf` |
| [`Hello.LibcLibrary`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.LibcLibrary) | a shared library that calls `strlen` through its PLT |
| [`consumer/Hello.Consumer`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/consumer) | an executable that consumes the `Hello.Static` package by version |
| [`Hello.Net`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Net) | a C# program that P/Invokes both shared libraries, and checks that it was given the `libc` build of `Hello.Library` |

`Samples.slnx` holds them all. `consumer/` sits outside the solution, because its package
must be packed first.

## How they are checked

[`tests/AsmSdk.Tests`](https://github.com/AsmFoundation/AsmSdk/tree/main/tests/AsmSdk.Tests),
an xunit v3 project, packs the SDK and the toolchain into a local feed, then builds the
samples against it, with a fresh `NUGET_PACKAGES`. Each of its 48 tests checks one
expectation:

- **the ELF properties of every output:** type, SONAME, `DT_NEEDED`, interpreter, exports
  and archive members;
- **that the executables run,** and that the C# program's calls into both libraries return
  the right values;
- **incremental builds:** a rebuild without changes runs no tool, and an include-file
  change reassembles what depends on it;
- **the package layouts** of a single-target and a multi-target library;
- **the projects in [`tests/projects`](https://github.com/AsmFoundation/AsmSdk/tree/main/tests/projects),**
  which cover what the samples cannot: the target framework compatibility errors (`NU1201`,
  `NU1202`), the errors for settings removed in 0.2.0, `StartupObject`, `ExportsFile`,
  `LinkerArg`, `DefineConstants` and `DebugSymbols`;
- **IDE integration:** the declared item types, that every file of a project folder is one
  item, the up-to-date inputs and outputs, and that a design-time build runs no tool (see
  [IDEs](../guide/ides.md)).

On Windows, the tests run the Linux outputs through WSL and the .NET sample in Docker, when
they are available; where neither is, those tests are skipped with the reason
(`ASMSDK_TEST_RUN` and `ASMSDK_TEST_DOTNET_RUN` choose explicitly). Only the .NET SDK is
needed:

```sh
dotnet run toolchain/fetch.cs -- --rid win-x64                 # or linux-x64 / osx-arm64: stages the tools
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj   # packs, builds the samples, checks and runs them
```

## Bytes on the home page

The block A on the home page is labelled with machine code from these functions:

- **the mint blocks** are `hello_add` from `Hello.Library`: `lea eax, [rdi + rsi]` assembles
  to `8D 04 37`, and `ret` to `C3`;
- **the teal blocks** are the first bytes of `sum_i32`, the freestanding template in
  [Writing NASM for AsmSdk](../guide/writing-nasm.md#templates).
