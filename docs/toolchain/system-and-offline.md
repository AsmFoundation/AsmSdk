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
  holds `nasm`, `lld` and `llvm-ar` (with `.exe` on Windows):

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
  <AsmLinkerCommand>ld.lld</AsmLinkerCommand>      <!-- or ld (GNU binutils) -->
  <AsmArchiverCommand>llvm-ar</AsmArchiverCommand>  <!-- or ar -->
</PropertyGroup>
```

With `AsmToolchain=System`, the SDK runs `nasm`, `ld` and `ar` from `PATH`, or the commands
you name.

**When to use it:**

- a link that needs a real Linux sysroot, such as a static glibc executable;
- a build machine with no toolchain package (Alpine, Linux arm64, Intel macOS);
- a policy that requires distribution-packaged tools.

**What changes:**

- **The link lines stay the same,** and GNU `ld` accepts them.
- **GNU `ld` resolves archives in command-line order,** unlike `ld.lld`. List
  `AsmLinkLibrary` items so that each library comes after the code that uses it.
- **NASM 2.16** (Ubuntu 24.04's `nasm` package) does not know the two warning names the SDK
  turns off. Every source then prints `nasm: warning: unknown warning name: reloc-rel-dword`.
  This is harmless: NASM still exits 0, even with `TreatWarningsAsErrors`.
- **NASM 2.16 lacks NASM 3's `implicit DEFAULT ABS is deprecated` warning.** A missing
  `default rel` then shows up only as a link error. Prefer NASM 3.

Any other `AsmToolchain` value fails with `ASM0002`.
