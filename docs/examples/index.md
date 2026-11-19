# Examples

Four complete programs in the repository's
[`samples/`](https://github.com/AsmFoundation/AsmSdk/tree/main/samples) folder, each the kind
of project you would actually write with AsmSdk. Each page walks through one: the project file,
the assembly that matters, and how the result is tested. Every fact these pages state is checked
by AsmSdk's own end-to-end tests ([`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs)).

| Example | What it is | What it shows |
|---|---|---|
| [Crc32C](crc32c.md) | CRC-32C (the iSCSI, ext4 and gRPC checksum) as a freestanding shared library for seven RIDs, called from C# | one x64 source for Windows, Linux and macOS through `asmsdk.inc`, an ARM64 source in GNU syntax, `ExportsFile`, run-time CPU dispatch on each OS, constants computed by the assemblers, a 3-way kernel (SSE4.2 + PCLMULQDQ, CRC32 + PMULL), `[LibraryImport]` with `SuppressGCTransition` |
| [Shipping to .NET through NuGet](nuget-for-dotnet.md) | Crc32C as two packages, and a .NET program that uses them by version | a native package with `runtimes/<rid>/native/` for seven RIDs, a managed package that depends on it, a consumer that never mentions assembly |
| [Testing and measuring from C#](testing-and-benchmarks.md) | Crc32C's xunit tests and BenchmarkDotNet benchmark | every code path against a C# reference, a deliberately broken constant caught, timings against `BitOperations.Crc32C` |
| [WordCount](wordcount.md) | `wc` in assembly, on glibc (`linux-x64`) | `open`, `read` and `printf` through the PLT, a libc data object through the GOT, `AsmLibcSymbol`, two sources sharing a record, SSE2 counting |
| [HexDump](hexdump.md) | `xxd` in assembly, with no libc at all (`linux-x64`) | `_start`, raw system calls, interrupted calls and short writes handled, a static executable of about 4 KB |

The smaller `Hello.*` samples are still the place to start: [Getting started](../guide/getting-started.md)
builds the same kinds of project in a few lines each, and [Samples](../ecosystem/samples.md) lists
them all.

## Building and running them

The samples build against the SDK of the checkout they are in: the end-to-end tests pack it into
a local feed first, then build and check every sample.

```sh
git clone https://github.com/AsmFoundation/AsmSdk && cd AsmSdk
dotnet run toolchain/fetch.cs -- --rid linux-x64                 # or win-x64, win-arm64, linux-arm64, osx-arm64
dotnet test --project tests/AsmSdk.Tests/AsmSdk.Tests.csproj     # packs, builds every sample, checks them
```

After that, the outputs are under each sample's `bin/Release/<target framework>/<rid>/`.
Crc32C's C# programs run on any of its seven platforms. WordCount and HexDump are Linux x64
programs: on Windows, run them in WSL or in a container.

**To use a sample in your own repository,** copy its folder (for Crc32C, the four `Crc32C*`
folders together) next to a `global.json` that names the SDK:

```json
{ "msbuild-sdks": { "AsmFoundation.Sdk": "0.3.0" } }
```

They need nothing else from the AsmSdk repository: restore gets the SDK and its toolchain from
nuget.org.

## How they are checked

[`ExampleTests`](https://github.com/AsmFoundation/AsmSdk/blob/main/tests/AsmSdk.Tests/ExampleTests.cs)
runs on every push, on the five build machines. Crc32C's C# programs run on each of them,
with that machine's own build; the Linux x64 programs run on the Linux x64 one:

- **Crc32C:** the exact list of exported names and their version, the constants NASM computed,
  both package layouts (seven RIDs) and the dependency between them, the library's own 21 unit
  tests, the benchmark's self-check, and the NuGet consumer's output.
- **WordCount:** what it imports from glibc, its counts against a C# count of the same files
  (including a word that spans two reads), and its error messages and exit statuses.
- **HexDump:** that it is static and under 8 KB, its output against a C# rendering of xxd's
  format, and its errors. It was also compared with `xxd` itself, byte for byte.
