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
| `.asmproj` → `.asmproj` | `ProjectReference` | the referenced project builds first; its public include folder joins the `-I` path; its `.a` (else `.so`) is linked |
| `.csproj` → `.asmproj` | `ProjectReference` | the asm project builds first; `lib<name>.so` is copied next to the program (on build and publish); no assembly reference |
| package → `.asmproj` | `PackageReference` | the package's `build/<id>.props` adds its `include/` folder and its library (`AsmOrigin="Package"`) |
| package → `.csproj` | `PackageReference` | NuGet copies `runtimes/linux-x64/native/lib<name>.so` for a `linux-x64` app; the asm items are ignored outside asm projects |

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
  2. **one link input:** the static library if one is produced (`StaticLibrary`, or
     `AsmProduceStaticLibrary=true`), otherwise the shared library.
- **Configuration:** `-c Release` builds the referenced project in Release too, as for
  any project reference.
- **Target framework:** NuGet picks it, as for a `.csproj`. A project that targets both
  `freestanding` and `libc` gives each referencing project its nearest build: a `libc`
  project gets the `libc` build, a `freestanding` project the `freestanding` one, and a
  .NET project the `libc` one. A `freestanding` project that references a `libc`-only
  project fails at restore with `NU1201`: freestanding code cannot link glibc code. The
  other way round is fine.
- **Kinds that don't fit:**
  - **A freestanding `Exe` must get a static library.** Given a `.so`, it fails with
    `attempted static link of dynamic object`.
  - **`Objects` projects can't be referenced yet.** The link fails with
    `cannot open <referencing project>/obj/…/x.o`, because the object paths
    are passed relative to the wrong project. Use `StaticLibrary`: the linker takes only
    the archive members it needs, so nothing is lost.
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
| `native/linux-x64/<tfm>/lib<name>.a` | the static library | `StaticLibrary`, or `AsmProduceStaticLibrary=true` |
| `runtimes/linux-x64/native/lib<name>.so` | the shared library | `Library`, single target framework |
| `runtimes/linux-x64/nativeassets/<framework>/lib<name>.so` | the shared library of each target framework | `Library`, `TargetFrameworks` |
| `src/**` | `AsmSource` and `AsmInclude` items, with their relative paths | `AsmPackSources=true` |
| `build/<framework>/<PackageId>.props`, `buildTransitive/<framework>/<PackageId>.props` | generated | one per target framework, always |

`<tfm>` is `freestanding` or `libc`, and `<framework>` the NuGet framework behind it:
`netstandard1.0` for `freestanding`, `netstandard2.0` for `libc`. NuGet only knows its own
frameworks, and this mapping makes its compatibility rules match the linker's.

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

## The generated consumer props

Each target framework's `obj/<Configuration>/<tfm>/asm/pack/<PackageId>.props` is packed as
both `build/<framework>/<PackageId>.props` and `buildTransitive/<framework>/<PackageId>.props`.
NuGet imports the one whose framework is nearest to the consumer's. For a `freestanding`
package with includes and a static library it reads:

```xml
<Project>
  <PropertyGroup>
    <AsmPackageRoot_Acme_Crc>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', '..'))</AsmPackageRoot_Acme_Crc>
  </PropertyGroup>
  <ItemGroup Condition="'$(UsingAsmFoundationSdk)' == 'true'">
    <AsmIncludeDirectory Include="$(MSBuildThisFileDirectory)../../include/" AsmOrigin="Package" AsmPackage="Acme.Crc" />
    <AsmLinkLibrary Include="$(MSBuildThisFileDirectory)../../native/linux-x64/freestanding/libcrc.a" AsmOrigin="Package" AsmPackage="Acme.Crc" />
  </ItemGroup>
</Project>
```

- **`AsmPackageRoot_<id>`:** characters of the id that are not letters, digits or `_` become
  `_`. The property points at the package root, so a consumer can assemble packed sources
  itself:
  `<AsmSource Include="$(AsmPackageRoot_Acme_Crc)src/crc_core.asm" />`. That needs
  `AsmPackSources=true` in the producer.
- **The items exist only in asm projects** (`UsingAsmFoundationSdk`). A `.csproj` consumer
  gets just the `runtimes/` folder, through NuGet's normal handling.
- **`AsmPackageLinkKind`** (in the producer) picks the `AsmLinkLibrary`:
  - `Static`, the default, takes the `.a` when one is packed, otherwise the `.so`;
  - `Shared` takes the `.so` when one is packed, otherwise the `.a`.
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
section .text
    ...
    call    crc_update wrt ..plt    ; from libcrc.a; a direct call once linked statically
```

- **Include order:** the consumer's own folders come first, then each package or
  reference in the order the items were added. Choose distinctive include file names.
- **Linking a package's `.a` into a `.so`** re-exports the archive's global symbols. Add
  `<LinkerArg Include="--exclude-libs ALL" />` when the `.so` should export only its own API.
- **Target frameworks:** a `libc` project can use a `freestanding` package, and a package
  built for both gives it the `libc` build. A `freestanding` project cannot use a
  `libc`-only package: restore fails with `NU1202`.
- **Freestanding executables** need the package's `.a`. A package that ships only a `.so`,
  or was packed with `AsmPackageLinkKind=Shared`, fails to link into them.
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
  - `global name:function (size)` for the API;
  - `hidden` for helpers, so they stay out of the dynamic symbol table and need no PLT
    inside the library.
- **A C header next to the `.inc`** (`include/<name>.h`) lets C, Rust and Python users bind
  the same functions. AsmSdk packs any file under `include/`.
- **Ship both libraries** (`Library` + `AsmProduceStaticLibrary=true`): .NET consumers get
  the `.so`, and asm consumers link the `.a` with no run-time dependency.
- **`AsmPackSources=true`** lets consumers re-assemble the code with their own defines, for
  example to inline or specialize it. It also makes the package's source its contract, so
  version accordingly.
