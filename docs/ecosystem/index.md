# The ecosystem

AsmSdk is part of the [AsmFoundation](https://github.com/AsmFoundation), which builds
backend infrastructure in hand-written x86-64 assembly as free, open-source software
(Apache-2.0). It is a product of its own: project, dependency and build management for any
assembly codebase. The projects below are some of its users.

## Packages

| Package | What it is |
|---|---|
| [`AsmFoundation.Sdk`](https://www.nuget.org/packages/AsmFoundation.Sdk) | The MSBuild SDK behind `<Project Sdk="AsmFoundation.Sdk">` (package type `MSBuildSdk`) |
| `AsmFoundation.Toolchain.<rid>` | NASM and the LLVM tools for one build machine. Restore downloads the right one; see [The toolchain](../toolchain/index.md) |
| Your own packages | Every `.asmproj` packs into a versioned NuGet package with `dotnet pack`; see [References and packages](../guide/references-and-packages.md) |

## Projects

| Project | What it is | Relation to AsmSdk |
|---|---|---|
| [KvAsm](https://github.com/AsmFoundation/KvAsm) | A gRPC key-value server in x86-64 assembly, with its own HTTP/2 engine, HPACK decoder and protobuf codec. It ships as a 2 MB container image, plus the `KvAsm.Client` .NET package. | Its unmodified server source builds with AsmSdk, as a glibc PIE, and passes KvAsm's whole end-to-end suite. |
| [GrpcAsm](https://github.com/AsmFoundation/GrpcAsm) | A gRPC server and client in assembly, with NASM, C, .NET and Python APIs. It generalizes KvAsm's engine. In development. | A solution of `.asmproj` and `.csproj` projects, built and packed with AsmSdk. |
| StorageAsm | Blob and S3-style object storage over gRPC on an assembly server. In design. | Planned to build with AsmSdk. |
| [AsmClaudeMarketplace](https://github.com/AsmFoundation/AsmClaudeMarketplace) | Claude Code plugins for the ecosystem, one per topic. | Its `asmsdk` plugin teaches Claude Code this SDK; see [Claude Code plugin](claude-code.md). |

## Principles the projects share

- **Measured, not argued.** When a design has a rival, a paired measurement picks the
  winner. Every public performance claim is recorded with its evidence and its scope.
- **Assembly where it pays.** Hand-written NASM for x86-64 Linux, with SIMD, AES-NI and
  carry-less multiply where profiles show they help, and plain code everywhere else.
- **Modern packaging:**
  - NuGet packages, through AsmSdk;
  - minimal `FROM scratch` container images;
  - .NET clients;
  - trusted publishing, with no stored keys.

## Contributing

Issues and pull requests are welcome in every repository. The organization's
[contributing guide](https://github.com/AsmFoundation/.github/blob/main/CONTRIBUTING.md)
applies to repositories without their own. Security reports go through each repository's
Security tab.
