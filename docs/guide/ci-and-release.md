# CI and releases

The goal is to build on every OS your developers use, run each platform's outputs on a
machine of that platform, and release packages from a tag without a stored secret. The
workflow below follows the AsmSdk repository's own CI and release workflows.


## A complete GitHub Actions workflow

```yaml
# .github/workflows/ci.yml
name: CI
on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        include:
          - { os: windows-2025,     rid: win-x64 }
          - { os: windows-11-arm,   rid: win-arm64 }
          - { os: ubuntu-24.04,     rid: linux-x64 }
          - { os: ubuntu-24.04-arm, rid: linux-arm64 }
          - { os: macos-15,         rid: osx-arm64 }
    runs-on: ${{ matrix.os }}
    env:
      DOTNET_NOLOGO: "1"
      NUGET_PACKAGES: ${{ github.workspace }}/.nuget/packages
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json      # only if global.json pins "sdk"; else dotnet-version: 10.0.x
      - uses: actions/cache@v4
        with:
          path: .nuget/packages              # holds the SDK and the toolchain package
          key: nuget-${{ runner.os }}-${{ hashFiles('global.json', '**/*.asmproj', '**/*.csproj', '**/Directory.*.props') }}
      - run: dotnet build App.slnx -c Release -warnaserror
      - run: dotnet test App.slnx -c Release --no-build    # each runner calls its own platform's build
      - name: Run this platform's program
        shell: bash
        run: ./HelloApp/bin/Release/libc/${{ matrix.rid }}/hello-app${{ runner.os == 'Windows' && '.exe' || '' }}
      - run: dotnet pack App.slnx -c Release -o artifacts/packages --no-build
        if: matrix.rid == 'linux-x64'
      - uses: actions/upload-artifact@v4
        if: matrix.rid == 'linux-x64'
        with:
          name: packages
          path: artifacts/packages/*.nupkg
```

## What each part is for

- **The matrix:** one runner per platform that has one. Every runner builds all the RIDs,
  since any build machine does, and the bundled toolchain differs per host. Each runner then
  runs its own platform's outputs:
  - `osx-x64` runs on `macos-15` too, under Rosetta 2, and `win-x64` on `windows-11-arm`,
    under Windows' x64 emulation;
  - `linux-musl-x64` runs on `ubuntu-24.04` in an Alpine container
    (`docker run … alpine`).

  GitHub's ARM64 runners (`ubuntu-24.04-arm`, `windows-11-arm`) are free for public
  repositories.
- **`NUGET_PACKAGES` plus the cache:** keeps the SDK and the toolchain (tens of MB) from
  being downloaded on every run. The key includes the project files because they decide
  which packages restore.
- **`-warnaserror`:** makes MSBuild warnings fail the build. NASM's own warnings count only
  if the projects set `<AsmFlags>-Xvc</AsmFlags>`, which turns them into MSBuild warnings,
  or `TreatWarningsAsErrors=true`, which makes NASM itself fail. Set both in
  `Directory.Build.props` for asm projects.
- **`--no-build` on `pack`:** packs exactly what was built and tested. One runner packs:
  every runner built the same seven RIDs.
- **Running the outputs:** a Linux freestanding executable runs on any Linux runner of its
  CPU. A `libc` one needs glibc 2.34+, which `ubuntu-22.04` and later have.
- **Tests that P/Invoke the library:** a test project's portable build carries every RID of
  the asm project, as an app does, so the same `dotnet test` on each runner calls that
  runner's own build.
- **Pre-release SDKs:** while a project uses an SDK version that is not on nuget.org, commit
  the folder feed, or download it in a step before the build, and keep a `nuget.config`
  mapping as in [References and packages](references-and-packages.md#feeds-and-publishing).
- **WebAssembly (not released yet):** every runner builds `browser-wasm` and `wasi-wasm` with
  the bundled tools. The outputs need a host to run in:
  - Node runs the modules, and is on every hosted runner;
  - wasmtime runs WASI programs: download its release archive for the runner and add it to
    `PATH`, and then `dotnet run` uses it too;
  - a .NET WebAssembly app's build needs `dotnet workload install wasm-tools` (browser) or
    `wasi-experimental` with the WASI SDK 25.0 in `WASI_SDK_PATH` (WASI). On Linux, install the
    .NET SDK into a folder the runner owns (`DOTNET_INSTALL_DIR` for `setup-dotnet`), because
    a workload installs into the SDK's own folder;
  - the workload belongs to the app's .NET version, not the SDK's. GitHub's Windows images come
    with SDK 11.0.100 and Visual Studio's `wasm-tools`, which serves `net11.0` only: a `net10.0`
    browser app there needs `dotnet workload install wasm-tools-net10`, and without it the SDK
    stops the build with ASM0054 instead of letting .NET build the app without its asm code. A
    `net10.0` WASI app has no workload on SDK 11.0.100 at all: pin SDK 10 in `global.json` for
    it.

  AsmSdk's own CI does all of this (see [WebAssembly](webassembly.md)): Linux x64 links both
  kinds of app, Windows x64 the browser ones.

## Versioning

- **One version for every package in a repository:** set `<Version>` in
  `Directory.Build.props`.
- **Version from the tag:** override it at release time with
  `dotnet pack -p:Version=${GITHUB_REF_NAME#v}`. A tag `v1.2.0` gives packages `1.2.0`.
- **Pre-releases:** `1.3.0-preview.1`. NuGet sorts them before `1.3.0`, and consumers opt
  in by asking for a pre-release version.
- **Semantic Versioning for asm libraries:**

  | Change | Version bump |
  |---|---|
  | export removed, or a contract broken (arguments, return value, a `struc` layout in `include/`) | major |
  | export added | minor |
  | implementation only | patch |

  `inspect_output.py --json` lists the exports, so a CI step can diff them against the
  previous release.

## Releasing to nuget.org with trusted publishing

Trusted publishing exchanges a short-lived GitHub OIDC token for a nuget.org API key valid
for one hour, so no secret is stored. Setup:

1. On nuget.org: **Account → Trusted Publishing → Add.** Fill in the owner (user or
   organization), the repository owner, the repository, and the workflow file name
   (`release.yml`). Optionally restrict it to a GitHub environment.
2. Write the workflow:

```yaml
# .github/workflows/release.yml
name: Release
on:
  push:
    tags: ['v*']
permissions:
  contents: write      # create the GitHub release
  id-token: write      # OIDC token for nuget.org
jobs:
  release:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet pack App.slnx -c Release -o artifacts/packages -p:Version=${GITHUB_REF_NAME#v}
      - run: ./HelloApp/bin/Release/libc/linux-x64/hello-app   # smoke-test before publishing
      - name: NuGet login (OIDC)
        id: login
        uses: NuGet/login@v1
        with:
          user: ${{ vars.NUGET_USER }}               # the nuget.org account or organization
      - run: dotnet nuget push "artifacts/packages/*.nupkg" -s https://api.nuget.org/v3/index.json -k ${{ steps.login.outputs.NUGET_API_KEY }} --skip-duplicate
      - run: gh release create "$GITHUB_REF_NAME" artifacts/packages/*.nupkg --generate-notes
        env:
          GH_TOKEN: ${{ github.token }}
```

- **`--skip-duplicate`:** a re-run of a partly published release does not fail on the
  packages already pushed.
- **Before the first push:** reserve the package id prefix on nuget.org, so nobody else
  can publish under your ids.

## Reproducibility

- **Deterministic outputs:** AsmSdk links with `--build-id=sha1` on Linux and `-Brepro` on
  Windows (the id and the timestamp computed from the content). ld64.lld derives a Mach-O
  file's UUID from the content too. Archives are written with `llvm-ar rcsD` (zero timestamps
  and ids). The same inputs give byte-identical outputs.
- **On Linux, the checkout folder is one of those inputs.** NASM records each source's
  **absolute path** in an ELF object's symbol table, and in DWARF in Debug builds, and ld.lld
  keeps it. A Release `.so` built from `C:/src/x` differs from one built from `/home/ci/x`.
  Measured from two folders: the `win-x64` DLL and the `osx-x64` dylib came out
  byte-identical, while the Linux `.so` files did not. For identical Linux outputs across
  machines, do one of these:
  - build from the same absolute path everywhere (a fixed CI workspace);
  - strip the static symbol table in Release: `<LinkerArg Include="-s" />` (Linux RIDs
    only) removes the paths and keeps the exported (dynamic) symbols.
- **Checking it:** build on two runners and compare SHA-256 hashes of the outputs. A
  difference means an input differs, such as a define, a toolchain version or a path.
- **Pinned inputs:**
  - `global.json` pins the SDK;
  - the SDK pins the toolchain, or `AsmToolchainVersion` does;
  - `packages.lock.json` with `--locked-mode` pins packages.
- **The .NET SDK version** matters little to the native output, but pin it with
  `global.json`'s `"sdk"` section anyway, for reproducible restores.

## Other CI systems

Any system that can run `dotnet` works:

- **Azure Pipelines:** `UseDotNet@2`, then the same commands. Cache `$(NUGET_PACKAGES)`
  with `Cache@2`.
- **GitLab CI:** the `mcr.microsoft.com/dotnet/sdk:10.0` image, a `cache:` entry for
  `.nuget/packages`, and the same commands. Its glibc also runs `linux-x64` `libc` outputs.
- **Jenkins or TeamCity:** the same commands on any agent. Each agent runs the outputs of
  its own platform.
