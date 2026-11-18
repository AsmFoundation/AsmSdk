# Extending the build

How the SDK plugs into MSBuild, what makes a build incremental, and how to add your own
steps without fighting it.


## Package layout

`AsmFoundation.Sdk.<version>.nupkg` (package type `MSBuildSdk`) contains:

```text
Sdk/Sdk.props                         imported first: switches off the managed build
Sdk/Sdk.targets                       imported last: wires in everything below
Sdk/AsmFoundation.Sdk.Version.props   generated at pack time: SDK and default toolchain versions
targets/AsmFoundation.Properties.targets   defaults, output names, toolchain selection
targets/AsmFoundation.Build.targets        items, toolchain checks, assemble, stubs, link
targets/AsmFoundation.References.targets   ProjectReference handling in both directions
targets/AsmFoundation.Pack.targets         native package layout and consumer props
stubs/libc.asm, stubs/crt1.asm             the libc link stub and start-up code
```

**Design choices:**

- **It rides on `Microsoft.NET.Sdk`,** the way Microsoft's NoTargets SDK does. That brings
  the standard restore, `PackageReference`, `ProjectReference`, `Version`, `dotnet pack`
  and solution builds.
- **It replaces only the steps that assume a managed assembly:** `CoreCompile`,
  `CreateManifestResourceNames`, `CopyFilesToOutputDirectory`,
  `GetTargetPathWithTargetPlatformMoniker`, `GetCopyToOutputDirectoryItems` and
  `GetCopyToPublishDirectoryItems`. MSBuild keeps the last definition of a target, so the
  SDK's files are imported after `Microsoft.NET.Sdk`'s targets.

## The target graph

```text
Build
└─ CoreBuild
   ├─ ResolveProjectReferences            (standard; builds referenced projects)
   └─ CoreCompile = AsmAssemble ; AsmLink
        AsmAssemble ── AsmPrepare ── _AsmResolveToolchain
                                  └─ _AsmResolveProjectReferences ── ResolveProjectReferences
        AsmLink ────── AsmAssemble, _AsmBuildLibcStub, _AsmBuildCrt
Pack
└─ _AsmPackNativeContent ── AsmPrepare, _AsmWriteConsumerProps
```

| Target | Inputs → outputs | Condition |
|---|---|---|
| `AsmAssemble` | `@(AsmSource)`, `@(_AsmDependency)`, the project file → `%(AsmSource.ObjectFile)`, batched per source | always |
| `_AsmBuildLibcStub` | `stubs/libc.asm`, the project file → `obj/…/asm/stubs/libc.so.6` | `TargetFramework=libc` and a shared library or executable |
| `_AsmBuildCrt` | `stubs/crt1.asm` → `obj/…/asm/stubs/crt1.o` | libc executable without `StartupObject` |
| `AsmLink` | `@(_AsmObject)`, `@(_AsmLinkInput)`, the `ExportsFile`, the project file → `$(TargetPath)`, `$(AsmStaticLibraryPath)` | `OutputType` is not `Objects` |

**`AsmPrepare` computes, once:**

- every object path, mirroring the source's subfolder, so `a/x.asm` and `b/x.asm` don't
  collide;
- the include directories, local ones first, each with a trailing `/`;
- the dependency list: `@(AsmInclude)`, plus every `**/*.inc` under include directories
  that carry `AsmOrigin`;
- the `-D` arguments from `DefineConstants`, to which `Properties.targets` has appended
  `DEBUG` or `RELEASE`, and `FREESTANDING` or `LIBC`;
- the `ExportsFile` (`--version-script`) and `LinkerArg` arguments of the link;
- the link inputs, from `@(AsmLinkLibrary)`.

**All paths handed to tools use forward slashes.** A trailing backslash before a closing
quote (`"C:\dir\"`) would escape the quote under Windows command-line rules and merge two
arguments into one.

## Incremental builds

- **An object is up to date** when it is newer than its source, every dependency `.inc`
  and the project file. Editing any `.inc` in the dependency list reassembles every source
  of the project. That is coarse, but never stale.
- **A link is up to date** when the output is newer than every object, every link input
  and the project file. A rebuild of a referenced library re-links its consumers.
- **A no-change rebuild runs no tool at all**. The one exception is the `chmod +x` on Linux and macOS hosts.
- **What is not tracked:**

  | Not tracked | Effect | Fix |
  |---|---|---|
  | `.inc` files in your own `AsmIncludeDirectory` items outside the project | stale objects | list them as `AsmInclude` items |
  | `incbin` files | stale objects | list them as `AsmInclude` items: any file works as a dependency |
  | include files without the `.inc` extension | stale objects | the same |
  | `Directory.Build.props` and other imported files | a changed define does not reassemble anything | rebuild (`dotnet build --no-incremental`) after changing them |

## How references are resolved

- **asm → asm:**
  1. `ResolveProjectReferences` builds the referenced project.
  2. `_AsmResolveProjectReferences` calls its `GetAsmLinkInputs` target.
  3. The returned items carry `AsmItemType`. `IncludeDirectory` items are added to
     `AsmIncludeDirectory`, and `LinkInput` items to `AsmLinkLibrary`, all with
     `AsmOrigin="Project"`.

  Transitive project references, which the .NET SDK adds, are processed the same way.
- **csproj → asm:**
  - The `.csproj` calls `GetTargetPathWithTargetPlatformMoniker`, which returns nothing,
    so no assembly reference is added and no `MSB3246` occurs.
  - It calls `GetCopyToOutputDirectoryItems` and `GetCopyToPublishDirectoryItems`, which
    return the `.so` with `TargetPath=lib<name>.so` and `PreserveNewest`.
- **Restore:** the `.asmproj`'s target frameworks stand for `netstandard1.0`
  (`freestanding`) and `netstandard2.0` (`libc`), which every `.csproj` may reference. A
  `.csproj` gets the `libc` build of a project that targets both. The asm project's own `PackageReference`s flow to the `.csproj` as transitive
  dependencies, as they would for any project.

## Extending the build

Hook your targets with `BeforeTargets` or `AfterTargets` on the public targets
(`AsmPrepare`, `AsmAssemble`, `AsmLink`). Underscore targets are internal and may change
between releases.

**Generate an include file before assembling**:

```xml
<Target Name="GenerateVersionInc" BeforeTargets="AsmPrepare">
  <WriteLinesToFile File="$(IntermediateOutputPath)gen/version.inc"
                    Lines="%25define BUILD_VERSION &quot;$(Version)&quot;"
                    Overwrite="true" WriteOnlyWhenDifferent="true" />
  <ItemGroup>
    <AsmIncludeDirectory Include="$(IntermediateOutputPath)gen/" />
    <AsmInclude Include="$(IntermediateOutputPath)gen/version.inc" />
    <FileWrites Include="$(IntermediateOutputPath)gen/version.inc" />
  </ItemGroup>
</Target>
```

- **`%25` is MSBuild's escape for `%`.** An unescaped `%de` would be decoded as byte
  `0xDE`.
- **`WriteOnlyWhenDifferent`** keeps the file's timestamp when its content is unchanged,
  so incremental builds stay incremental.
- **Run it before `AsmPrepare`,** so that the items exist when the include path and the
  dependencies are computed.

**Generate a source file** (for example a table computed by a .NET 10 file-based app):

```xml
<Target Name="GenerateTables" BeforeTargets="AsmPrepare" Inputs="tools/gen_tables.cs" Outputs="$(IntermediateOutputPath)gen/tables.asm">
  <MakeDir Directories="$(IntermediateOutputPath)gen" />   <!-- a clean obj/ has no gen/ yet -->
  <Exec Command="dotnet run tools/gen_tables.cs -- &quot;$(IntermediateOutputPath)gen/tables.asm&quot;" />
  <ItemGroup>
    <AsmSource Include="$(IntermediateOutputPath)gen/tables.asm" />
    <FileWrites Include="$(IntermediateOutputPath)gen/tables.asm" />
  </ItemGroup>
</Target>
```

With `Inputs` and `Outputs` on a target, MSBuild still runs the target's item groups when
it skips the target as up to date. The `AsmSource` item exists either way.

**Keep tools out of design-time builds.** IDEs run `Compile` and `CoreCompile` on every
project load with `DesignTimeBuild=true` (see [IDEs](ides.md)). A target hooked to
`AsmPrepare`, `AsmAssemble` or `AsmLink`, as above, never runs then: `CoreCompile`, which
leads to them, is skipped. A target hooked anywhere else that runs a tool needs
`Condition="'$(DesignTimeBuild)' != 'true'"`.

**Post-link steps** (strip a copy, sign, compute a checksum):

```xml
<Target Name="StripCopy" AfterTargets="AsmLink" Condition="'$(Configuration)' == 'Release'">
  <Copy SourceFiles="$(TargetPath)" DestinationFiles="$(TargetDir)debug/$(TargetFileName)" />
  <!-- the shipped file is linked with -s via a LinkerArg item; the copy keeps symbols for debugging -->
</Target>
```

**Change a link line:**

- Use `LinkerArg` items for flags, `ExportsFile` for a version script, and `AsmLinkLibrary`
  items for inputs.
- Don't override `AsmLink`. To change the whole link, set
  `<OutputType>Objects</OutputType>` and link in your own target, after
  `AsmAssemble`, from `@(AsmSource->'%(ObjectToolPath)')`.

## Inspecting a build

- **`dotnet build -v:n`** prints every NASM and linker command exactly as run. Copy one
  and re-run it from the project folder to experiment.
- **`dotnet msbuild X.asmproj -getProperty:TargetFileName -getProperty:AsmToolchainDirectory`**
  shows evaluated properties, without building.
- **`dotnet msbuild X.asmproj -getItem:AsmSource -getItem:AsmIncludeDirectory`** shows the
  items, as JSON.
- **`dotnet build -bl`** writes `msbuild.binlog`. Open it in the MSBuild Structured Log
  Viewer to see every target, its skip reason (up to date) and every property.
- **[`inspect_elf.py`](../ecosystem/claude-code.md#inspect_elfpy)** shows what came out.
