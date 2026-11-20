# References and packages

Assembly code is shared the way .NET code is shared:

- **inside a solution,** with `ProjectReference`;
- **across repositories and teams,** as NuGet packages pinned by version, with
  `PackageReference`.

Restore, feeds, version ranges, lock files, Central Package Management and package source
mapping all work as they do for a `.csproj`, because the SDK rides on `Microsoft.NET.Sdk`.


## The four flows

| From → to | Mechanism | What happens |
|---|---|---|
| `.asmproj` → `.asmproj` | `ProjectReference` | the referenced project builds first, for the same RID; its public include folder joins the `-I` path; its static library (else its shared one, through the import library on Windows) is linked |
| `.csproj` → `.asmproj` | `ProjectReference` | the asm project builds first, for the RIDs the build needs; a portable build gets every RID's library under `runtimes/<rid>/native/` and in `deps.json`, a RID-specific build the best match beside the program (on build and publish), or wherever `BinariesCopyPath` says; no assembly reference. `dotnet pack` of the .NET project makes it a package dependency, or, with `PrivateAssets="all"`, packs the libraries into the .NET package |
| package → `.asmproj` | `PackageReference` | the package's `build/<id>.props` adds its `include/` folder and the library of the project's RID (`AsmOrigin="Package"`) |
| package → `.csproj` | `PackageReference` | NuGet copies the `runtimes/<rid>/native/` libraries, as for any native package; the asm items are ignored outside asm projects. `BinariesCopyPath` on the `PackageReference` (or the property) moves them, by targets inside the package |

See [Where the binaries go](interop.md#where-the-binaries-go-binariescopypath) for
`BinariesCopyPath` and `PrivateAssets="all"`.

## ProjectReference between asm projects

```xml
<ItemGroup>
  <ProjectReference Include="../Crc/Crc.asmproj" />
</ItemGroup>
```

- **What the reference sees:** the referencing project calls the referenced one's
  `GetAsmLinkInputs` target, which returns:
  1. the **public include directories:** `include/` when it exists, otherwise the project
     folder;
  2. **one link input,** of the RID being built: the static library if one is produced
     (`StaticLibrary`, or `AsmProduceStaticLibrary=true`), otherwise the shared library (on
     Windows, the DLL's import library).
- **RIDs:** each inner build of the referencing project builds the reference for its own RID.
  A referenced project that lists no RIDs of its own is built for whatever its referencing
  projects ask for; one that lists some must list the RID asked for (`ASM0012`).
- **Configuration:** `-c Release` builds the referenced project in Release too, as for
  any project reference.
- **Target framework:** NuGet picks it, as for a `.csproj`. A project that targets both
  `freestanding` and `libc` gives each referencing project its nearest build: a `libc`
  project gets the `libc` build, a `freestanding` project the `freestanding` one, and a
  .NET project the `libc` one. A `freestanding` project that references a `libc`-only
  project fails at restore with `NU1201`: freestanding code cannot link C library code. The
  other way round is fine.
- **Kinds that don't fit:**
  - **A Linux freestanding `Exe` must get a static library.** Given a `.so`, it fails with
    `attempted static link of dynamic object`.
  - **`Objects` projects** link their object files into the referencing project (0.1.0 and
    0.2.0 passed their paths relative to the wrong project, and the link failed). A
    `StaticLibrary` does the same job, and the linker takes only the archive members it
    needs.
- **Transitive references flow**. For `App → Lib → Base`, the .NET SDK adds
  `Base` as a transitive project reference. `App` then assembles with
  `-I Lib/include/ -I Base/include/` and links `libLib.a libBase.a`, in that order.
  - This is needed: a static `Lib` does not contain `Base`'s code.
  - It also means `App` sees `Base`'s include files without asking for them.
  - `<DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>` turns
    it off. `App` must then reference `Base` itself.
- **Mixed solutions:** a `ProjectReference` from an `.asmproj` to a `.csproj` (for
  example, a code generator) is ignored by the asm reference logic and behaves as a normal
  build-order reference.

## Packing

```xml
<PropertyGroup>
  <OutputType>StaticLibrary</OutputType>        <!-- or Library (+ AsmProduceStaticLibrary) -->
  <AssemblyName>crc</AssemblyName>
  <PackageId>Acme.Crc</PackageId>
  <Version>1.2.0</Version>
  <Authors>Acme</Authors>
  <Description>CRC-32C in x86-64 assembly (SSE4.2 and carry-less multiply).</Description>
  <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <RepositoryUrl>https://github.com/acme/crc</RepositoryUrl>
</PropertyGroup>
<ItemGroup>
  <None Include="README.md" Pack="true" PackagePath="/" />
</ItemGroup>
```

`dotnet pack` (or `dotnet pack -c Release -o artifacts/packages`) writes this layout:

| Package path | From | Condition |
|---|---|---|
| `include/**` | `AsmPublicInclude` items (default `include/**/*`) | the project has an `include/` folder, or declares items; packed once whatever the number of target frameworks |
| `lib/<framework>/_._` | an empty marker | one per target framework, always |
| `native/<rid>/<tfm>/lib<name>.a`, `<name>.lib` | the static library, per RID | `StaticLibrary`, or `AsmProduceStaticLibrary=true` |
| `native/<rid>/<tfm>/<name>.dll.lib` | a Windows DLL's import library | `Library`, Windows RIDs |
| `runtimes/<rid>/native/` | the shared library, per RID: `<name>.dll`, `lib<name>.so`, `lib<name>.dylib` | `Library`, single target framework |
| `runtimes/<rid>/nativeassets/<framework>/` | the shared library of each target framework | `Library`, `TargetFrameworks` |
| `src/**` | `AsmSource` and `AsmInclude` items, with their relative paths | `AsmPackSources=true` |
| `build/<framework>/<PackageId>.props`, `buildTransitive/<framework>/<PackageId>.props` | generated | one per target framework, always |
| `build/<framework>/<PackageId>.targets`, `buildTransitive/<framework>/<PackageId>.targets` | generated, plain MSBuild: `BinariesCopyPath` for .NET consumers, and the fallback loader ([Where the binaries go](interop.md#where-the-binaries-go-binariescopypath)) | a shared library is packed under `runtimes/` |

`<tfm>` is `freestanding` or `libc`, and `<framework>` the NuGet framework behind it:
`netstandard1.0` for `freestanding`, `netstandard2.0` for `libc`. NuGet only knows its own
frameworks, and this mapping makes its compatibility rules match the linker's. `<rid>` is each
RID the project builds: `dotnet pack` builds them all. The implicit `AsmFoundation.Sdk`
dependency every `.asmproj` carries is removed from the `.nuspec`.

**Footguns:**

- **No `include/` folder means no include files in the package**. A
  `ProjectReference` still worked, because it falls back to the project folder. Consumers
  of the package then fail with `unable to open include file`. Move public files to
  `include/`, or declare `<AsmPublicInclude Include="crc.inc" />` explicitly.
- **The `.a` is named after `AssemblyName`** (which defaults to the project name), not
  after `PackageId`. `Acme.Crc` built from `Crc.asmproj` ships `libCrc.a` unless you set
  `AssemblyName`.
- **`lib/` holds only the empty `_._` markers.** NuGet treats `lib/` as managed assemblies,
  so the static library lives under `native/`. The markers are there because NuGet judges a
  package's compatibility by `lib/`: a package without one counts as compatible with every
  framework, and a `freestanding` project would restore a `libc`-only package, import
  nothing and fail at link time. With the markers, that restore fails with `NU1202`.
- **The package declares one dependency group per target framework** (`netstandard1.0`,
  `netstandard2.0`), which every .NET consumer accepts. Its own `PackageReference`s become
  package dependencies, as usual.
- **Its `ProjectReference`s become package dependencies too,** on each referenced project's
  `PackageId` and version, as NuGet does for any project reference. A static library linked
  *into* a shared library or a program needs no such dependency, because its code is already
  inside. Without `PrivateAssets="all"` on that reference, the package still depends on the
  static library's package. When that package was never published, every consumer fails
  restore with `NU1101 Unable to find package`:

  ```xml
  <ProjectReference Include="../CrcCore/CrcCore.asmproj" PrivateAssets="all" />  <!-- linked in -->
  ```

  A `StaticLibrary` package keeps the dependency: its consumers link both archives.

## The generated consumer props

Each target framework's `obj/<Configuration>/<tfm>/asm/pack/<PackageId>.props` is packed as
both `build/<framework>/<PackageId>.props` and `buildTransitive/<framework>/<PackageId>.props`.
NuGet imports the one whose framework is nearest to the consumer's. For a `freestanding`
package with includes and a static library packed for `linux-x64`, `win-x64` and `osx-arm64`,
it reads (comment shortened):

```xml
<Project>
  <!-- Generated by AsmFoundation.Sdk for Acme.Crc 1.0.0, target framework freestanding (netstandard1.0), packed for linux-x64, win-x64, osx-arm64. … -->
  <PropertyGroup>
    <AsmPackageRoot_Acme_Crc>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', '..'))</AsmPackageRoot_Acme_Crc>
  </PropertyGroup>
  <PropertyGroup Condition="'$(UsingAsmFoundationSdk)' == 'true' and '$(RuntimeIdentifier)' != ''">
    <_AsmPackageRid_Acme_Crc Condition="'$(RuntimeIdentifier)' == 'win-x64'">win-x64</_AsmPackageRid_Acme_Crc>
    <_AsmPackageRid_Acme_Crc Condition="'$(RuntimeIdentifier)' == 'linux-x64'">linux-x64</_AsmPackageRid_Acme_Crc>
    <_AsmPackageRid_Acme_Crc Condition="'$(RuntimeIdentifier)' == 'linux-musl-x64'">linux-x64</_AsmPackageRid_Acme_Crc>
    <_AsmPackageRid_Acme_Crc Condition="'$(RuntimeIdentifier)' == 'osx-arm64'">osx-arm64</_AsmPackageRid_Acme_Crc>
  </PropertyGroup>
  <ItemGroup Condition="'$(UsingAsmFoundationSdk)' == 'true'">
    <AsmIncludeDirectory Include="$(MSBuildThisFileDirectory)../../include/" AsmOrigin="Package" AsmPackage="Acme.Crc" />
    <AsmLinkLibrary Include="$(MSBuildThisFileDirectory)../../native/linux-x64/freestanding/libcrc.a" Condition="'$(_AsmPackageRid_Acme_Crc)' == 'linux-x64'" AsmOrigin="Package" AsmPackage="Acme.Crc" />
    <AsmLinkLibrary Include="$(MSBuildThisFileDirectory)../../native/osx-arm64/freestanding/libcrc.a" Condition="'$(_AsmPackageRid_Acme_Crc)' == 'osx-arm64'" AsmOrigin="Package" AsmPackage="Acme.Crc" />
    <AsmLinkLibrary Include="$(MSBuildThisFileDirectory)../../native/win-x64/freestanding/crc.lib" Condition="'$(_AsmPackageRid_Acme_Crc)' == 'win-x64'" AsmOrigin="Package" AsmPackage="Acme.Crc" />
  </ItemGroup>
  <Target Name="_AsmCheckPackage_Acme_Crc" BeforeTargets="AsmPrepare" Condition="'$(UsingAsmFoundationSdk)' == 'true' and '$(RuntimeIdentifier)' != '' and '$(_AsmPackageRid_Acme_Crc)' == ''">
    <Error Code="ASM0060" Text="The package Acme.Crc 1.0.0 has no build for $(RuntimeIdentifier) (target framework freestanding): it was packed for linux-x64, win-x64, osx-arm64. …" />
  </Target>
</Project>
```

- **Each RID of the consumer gets the package's nearest build,** by the RID graph's fallbacks
  at pack time: `linux-musl-x64` takes the `linux-x64` library here. A RID with no build is
  `ASM0060`, before anything is assembled.

- **`AsmPackageRoot_<id>`:** characters of the id that are not letters, digits or `_` become
  `_`. The property points at the package root, so a consumer can assemble packed sources
  itself:
  `<AsmSource Include="$(AsmPackageRoot_Acme_Crc)src/crc_core.asm" />`. That needs
  `AsmPackSources=true` in the producer.
- **The items exist only in asm projects** (`UsingAsmFoundationSdk`). A `.csproj` consumer
  gets just the `runtimes/` folder, through NuGet's normal handling.
- **`AsmPackageLinkKind`** (in the producer) picks the `AsmLinkLibrary`:
  - `Static`, the default, takes the static library when one is packed, otherwise the
    shared one (on Windows, the DLL's import library);
  - `Shared` takes the shared one when one is packed, otherwise the static one.
- **`buildTransitive/`** makes the props apply to projects that get the package
  transitively, through another package or a referenced project.

## Consuming packages

```xml
<ItemGroup>
  <PackageReference Include="Acme.Crc" Version="1.2.0" />
</ItemGroup>
```

```nasm
%include "crc.inc"                  ; found through the package's include/ folder
default rel
%include "asmsdk.inc"
section .text
    ...
    call    crc_update PLT          ; from libcrc.a; a direct call once linked statically
```

- **Include order:** the consumer's own folders come first, then each package or
  reference in the order the items were added. Choose distinctive include file names.
- **Linking a package's static library into a shared one** re-exports the archive's
  exports: its global symbols on Linux and macOS, its `FUNCTION` directives on Windows. The
  producer avoids it with `HIDDEN_FUNCTION` for its internals; the consumer with an
  `ExportsFile`, or on Linux `<LinkerArg Include="--exclude-libs ALL" />`.
- **Target frameworks:** a `libc` project can use a `freestanding` package, and a package
  built for both gives it the `libc` build. A `freestanding` project cannot use a
  `libc`-only package: restore fails with `NU1202`.
- **Linux freestanding executables** need the package's static library. A package that ships
  only a `.so`, or was packed with `AsmPackageLinkKind=Shared`, fails to link into them.
- **Restore failures:** `NU1101 Unable to find package` means a feed is missing, or package
  source mapping routes the id to a feed that lacks it (see below).

## Versions and dependency management

- **Versions:** `Version` (or `PackageVersion`) is the package version, written with
  Semantic Versioning: a breaking ABI change, such as a removed export or a changed
  argument meaning, needs a new major version.
- **Ranges:**
  - `Version="1.2.0"` means "1.2.0 or newer, the lowest that satisfies".
  - `Version="[1.2.0]"` pins exactly.
  - `Version="[1.2,2.0)"` allows minor updates.
- **Central Package Management works**:

  ```xml
  <!-- Directory.Packages.props -->
  <Project>
    <PropertyGroup>
      <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    </PropertyGroup>
    <ItemGroup>
      <PackageVersion Include="Acme.Crc" Version="1.2.0" />
    </ItemGroup>
  </Project>
  ```

  Each project then writes `<PackageReference Include="Acme.Crc" />` without a version.
- **Lock files:** `RestorePackagesWithLockFile=true` writes `packages.lock.json`. CI then
  restores with `--locked-mode` and fails on drift.
- **The toolchain is not a dependency.** It comes through `PackageDownload` and pins
  `AsmFoundation.Toolchain.<host>` exactly. Consumers of your package download their own
  toolchain, the one their SDK version names.

## Feeds and publishing

**A folder feed** is the simplest feed for development and CI:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="Acme.*" />
      <package pattern="AsmFoundation.*" />   <!-- only while the SDK is not on nuget.org -->
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

**Package source mapping:**

- **Mappings merge across `nuget.config` files** up the folder tree (and the user-level
  file). A mapping in a parent folder routes ids to its sources, and a source that is not
  mapped for an id is never searched for it. A `<clear />` inside
  `<packageSourceMapping>` starts the mapping afresh.
- **The most specific pattern wins:** `Acme.*` beats `*`.

**Publishing:**

- **nuget.org:** `dotnet nuget push artifacts/packages/*.nupkg -s https://api.nuget.org/v3/index.json -k <key>`.
  In CI, prefer trusted publishing (no stored key; see [CI and releases](ci-and-release.md)).
- **Private feeds:** GitHub Packages, Azure Artifacts, or any NuGet v3 server. Push with
  `dotnet nuget push -s <feed>`, and add the feed and its credentials to `nuget.config`, or
  use the `NuGet/setup-nuget` and `actions/setup-dotnet` authentication options.
- **Reserve a prefix on nuget.org** (for example `Acme.*`) so nobody else can publish
  under your ids.

## Designing a package's public surface

- **`include/<name>.inc`:**
  - an include guard;
  - `extern` declarations for every exported function;
  - `%define` constants;
  - `struc` layouts;
  - macros consumers need.

  Prefix every name, because NASM has a single global namespace.
- **Exports:**
  - `FUNCTION` (from `asmsdk.inc`, or `asmsdk-gnu.inc`) for the API, on every OS;
  - `HIDDEN_FUNCTION` for helpers other files call, so they stay out of every image's
    exports, and `LOCAL_FUNCTION` for helpers of one file.
- **A C header next to the `.inc`** (`include/<name>.h`) lets C, Rust and Python users bind
  the same functions. AsmSdk packs any file under `include/`.
- **Ship both libraries** (`Library` + `AsmProduceStaticLibrary=true`): .NET consumers get
  the shared library, and asm consumers link the static one with no run-time dependency.
- **List every RID the code supports.** Consumers can only use the RIDs you packed; an asm
  consumer on another one gets `ASM0060`, and a .NET one `DllNotFoundException`.
- **`AsmPackSources=true`** lets consumers re-assemble the code with their own defines, for
  example to inline or specialize it. It also makes the package's source its contract, so
  version accordingly.

**Example:** [Crc32C](../examples/crc32c.md#the-exports-file) keeps its binary interface to five versioned names with an `ExportsFile`, and [Shipping to .NET through NuGet](../examples/nuget-for-dotnet.md) splits it into a native and a managed package.
