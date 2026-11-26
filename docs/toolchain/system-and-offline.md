# Pinning, offline and system tools

## Pin a toolchain version

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <AsmToolchainVersion>0.1.0</AsmToolchainVersion>
</PropertyGroup>
```

Without this, each project uses the version its SDK release was tested with. Pinning keeps
the tools fixed across SDK upgrades, until you change the pin.

## Offline builds

Restore once with network access. After that, the global packages folder satisfies every
build. In CI, cache that folder; [CI and releases](../guide/ci-and-release.md) has a
workflow that does.

> [!NOTE]
> `ASM0001` means the toolchain is not where restore puts it. Common causes:
>
> - a build with `--no-restore` on a machine that never restored;
> - a cleared NuGet cache (`dotnet nuget locals all --clear`);
> - `NUGET_PACKAGES` set to different folders at restore time and at build time.

## Air-gapped builds

- **Mirror the packages.** Put `AsmFoundation.Sdk.<v>.nupkg` and
  `AsmFoundation.Toolchain.<rid>.<v>.nupkg` in an internal feed or folder feed, and map
  `AsmFoundation.*` to it in `nuget.config`:

  ```xml
  <configuration>
    <packageSources>
      <clear />
      <add key="internal" value="\\fileserver\nuget" />
    </packageSources>
  </configuration>
  ```

- **Skip the toolchain download entirely.** Point `AsmToolchainDirectory` at a folder that
  holds `nasm`, `llvm-mc`, `lld` and `llvm-ar` (with `.exe` on Windows):

  ```xml
  <AsmToolchainDirectory>/opt/asm-tools/</AsmToolchainDirectory>
  ```

  No `PackageDownload` is added, and the SDK runs the tools from that folder.

The SDK package itself is resolved through `global.json`'s `msbuild-sdks` from the same
feeds. If it cannot be found, the build fails with
`MSB4236: The SDK 'AsmFoundation.Sdk' specified could not be found`.

## System toolchain

```xml
<PropertyGroup>
  <AsmToolchain>System</AsmToolchain>
  <!-- optional: full commands, with arguments -->
  <AsmNasmCommand>/usr/local/bin/nasm</AsmNasmCommand>
  <AsmLlvmMcCommand>llvm-mc</AsmLlvmMcCommand>
  <AsmLinkerCommand>ld.lld</AsmLinkerCommand>      <!-- or ld (GNU binutils), for Linux RIDs -->
  <AsmLinkerCommand Condition="$(RuntimeIdentifier.StartsWith('win-'))">lld-link</AsmLinkerCommand>
  <AsmLinkerCommand Condition="$(RuntimeIdentifier.StartsWith('osx-'))">ld64.lld</AsmLinkerCommand>
  <AsmArchiverCommand>llvm-ar</AsmArchiverCommand>  <!-- or ar -->
</PropertyGroup>
```

With `AsmToolchain=System`, the SDK runs `nasm`, `llvm-mc`, `ld` and `ar` from `PATH`, or the
commands you name.

**When to use it:**

- a link that needs a real Linux sysroot, such as a static glibc executable;
- a build machine with no toolchain package (Alpine, Intel macOS): Homebrew's and Alpine's
  `nasm`, `llvm` and `lld` packages have every tool;
- a policy that requires distribution-packaged tools.

**What changes:**

- **Windows and macOS RIDs need an LLVM linker.** The default, `ld`, is GNU `ld`, which links
  ELF only, so a Windows or macOS RID without `AsmLinkerCommand` stops with `ASM0005`. Name
  `lld-link` (it also writes the SDK's import libraries, as `lld-link -lib`) or `ld64.lld`,
  conditioned on the RID as above.
- **So does the Linux RID of the other architecture.** A distribution's GNU `ld` links its own
  machine's ELF only (Ubuntu x64's has no AArch64 emulation, and stops with "file in wrong
  format"), so `linux-arm64` on an x64 machine, or `linux-x64` on an ARM64 one, stops with
  `ASM0005` before any tool runs. `ld.lld` links every architecture, as in the example above;
  a cross `ld` such as `aarch64-linux-gnu-ld` works when named. A static library never links,
  and is not checked.
- **The link lines stay the same,** and GNU `ld` accepts the Linux ones for its own architecture.
- **GNU `ld` resolves archives in command-line order,** unlike `ld.lld`. List
  `NativeLibrary` items so that each library comes after the code that uses it.
- **NASM 2.16** (Ubuntu 24.04's `nasm` package) does not know the two warning names the SDK
  turns off. Every source then prints `nasm: warning: unknown warning name: reloc-rel-dword`.
  This is harmless: NASM still exits 0, even with `TreatWarningsAsErrors`.
- **NASM 2.16 lacks NASM 3's `implicit DEFAULT ABS is deprecated` warning.** A missing
  `default rel` then shows up only as a link error. Prefer NASM 3.

Any other `AsmToolchain` value fails with `ASM0002`.
