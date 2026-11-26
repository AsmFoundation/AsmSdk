# AsmFoundation.Templates

`dotnet new` templates for assembly projects built with
[AsmFoundation.Sdk](https://www.nuget.org/packages/AsmFoundation.Sdk): an `.asmproj` sits in a
solution next to `.csproj` projects, builds with `dotnet build`, and is called from C# through
the bindings the SDK writes from its `PROTO` lines, or through `[LibraryImport]`.

```sh
dotnet new install AsmFoundation.Templates
dotnet new asmnet -n Demo          # a C# app, the assembly library it calls, and xunit tests
cd Demo
dotnet run --project Demo.App      # add_ints(40, 2) = 42
dotnet test
```

| Template | Short name | What it creates |
|---|---|---|
| Assembly Library | `asmlib` | a shared library: x64 code in NASM (`add.asm`), ARM64 code in GNU syntax (`add.s`) |
| Assembly Console App | `asmexe` | a program on the platform's C library; `dotnet run` builds and runs it |
| Assembly Static Library | `asmstatic` | a static library with a public `include/` folder, for other assembly projects |
| Assembly + .NET Solution | `asmnet` | a `.slnx` with a C# console app, the assembly library, and xunit tests |
| Assembly WebAssembly Library | `asmwasm` | a WebAssembly library (`add.wasm.s`) for `browser-wasm` and `wasi-wasm`, and a `run.mjs` that calls it from Node |
| Assembly + .NET WebAssembly Solution | `asmwasmapp` | a `.slnx` with a .NET `browser-wasm` app and the WebAssembly library it calls (needs the `wasm-tools` workload) |

- **Builds for this machine first.** A project that lists no `RuntimeIdentifiers` builds for the
  build machine's platform. List the platforms to ship in the project:
  `<RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>`.
- **Sources are just code.** The SDK's platform layer (`FUNCTION`, `ARG1`, `RETURN`, ...) comes
  with `ImplicitUsings`, on by default.
- **C# bindings from `PROTO` lines.** `PROTO int32_t add_ints(int32_t a, int32_t b)` beside a
  `FUNCTION` gives every C# project that references the library a binding, written by the SDK:
  `Demo.Native.Add.AddInts(40, 2)` (the library's namespace, the class of `add.asm`). `asmnet`
  and `asmwasmapp` call their libraries so, with no `[LibraryImport]`.
- **`dotnet sln add`** adds an `.asmproj` like any project. `dotnet add reference` does not accept
  one yet (a .NET CLI limitation with target framework aliases); write the line instead:
  `<ProjectReference Include="../Native/Native.asmproj" />`.
- **`--sdk-version`** picks the AsmFoundation.Sdk version the projects name; it defaults to this
  package's version.
- **WebAssembly:** `dotnet new asmwasm -n Calc`, `dotnet build`, then `node Calc/run.mjs` prints
  `add_ints(40, 2) = 42`. `dotnet new asmwasmapp -n Calc` makes the .NET side too:
  `dotnet workload install wasm-tools`, then `dotnet run --project Calc/Calc.App`.

Documentation: <https://asmfoundation.github.io/AsmSdk/>.
