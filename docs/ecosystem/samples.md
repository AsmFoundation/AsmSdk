# Samples

The [`samples/`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples) folder of the
AsmSdk repository covers every kind of project. CI builds and checks all of them on Windows,
Linux and macOS.

| Sample | Shows |
|---|---|
| [`Hello.Library`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Library) | a freestanding shared library (`libhello.so`) with a public `include/` file |
| [`Hello.Static`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Static) | a static library (`libhellostatic.a`) with public includes, packed as the NuGet package `Hello.Static` 1.0.0 |
| [`Hello.App`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.App) | a freestanding executable that links `Hello.Static` through a `ProjectReference` |
| [`Hello.Libc`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Libc) | a `main()` program on glibc that calls `printf` |
| [`Hello.LibcLibrary`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.LibcLibrary) | a shared library that calls `strlen` through its PLT |
| [`consumer/Hello.Consumer`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/consumer) | an executable that consumes the `Hello.Static` package by version |
| [`Hello.Net`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples/Hello.Net) | a C# program that P/Invokes both shared libraries |

`Samples.slnx` holds them all. `consumer/` sits outside the solution, because its package
must be packed first.

## How they are checked

[`tests/run.py`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/run.py) packs the
SDK and the toolchain into a local feed, then builds the samples against it, with a fresh
`NUGET_PACKAGES`. Then it checks:

- **the ELF properties of every output:** type, SONAME, `DT_NEEDED`, interpreter, exports
  and archive members;
- **that the executables run,** and that the C# program's calls into both libraries return
  the right values;
- **incremental builds:** a rebuild without changes runs no tool, and an include-file
  change reassembles what depends on it.

On Windows, `tests/run.py` runs the Linux outputs through WSL and the .NET sample in
Docker, when they are available.

```sh
python toolchain/fetch.py --rid win-x64     # or linux-x64 / osx-arm64: stages the tools
python tests/run.py                         # packs, builds the samples, checks and runs them
```

## Bytes on the home page

The block A on the home page is labelled with machine code from these functions:

- **the mint blocks** are `hello_add` from `Hello.Library`: `lea eax, [rdi + rsi]` assembles
  to `8D 04 37`, and `ret` to `C3`;
- **the teal blocks** are the first bytes of `sum_i32`, the freestanding template in
  [Writing NASM for AsmSdk](../guide/writing-nasm.md#templates).
