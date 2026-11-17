#!/usr/bin/env python3
"""End-to-end test of AsmFoundation.Sdk: pack it, build the samples, check every output.

Usage:
    python tests/run.py [--rids win-x64,linux-x64,osx-arm64] [--run auto|native|wsl|none]
                        [--dotnet-run auto|native|docker|none]

Steps (each one prints PASS/FAIL lines; the exit code is the number of failures, capped at 1):
  1. Pack AsmFoundation.Sdk, plus the toolchain package for every RID whose tools are staged
     in artifacts/toolchain/<rid>/tools (toolchain/fetch.py stages them). The package for
     this machine must be among them.
  2. Build samples/Samples.slnx in Release, against a fresh NuGet cache in
     artifacts/test-nuget, so the packages under test are always the ones just packed (the
     version number does not change between iterations, a shared cache would keep the
     first copy).
  3. Check the ELF properties of every output with the bundled llvm-readobj: file type,
     SONAME, DT_NEEDED, interpreter, exported symbols, archive members.
  4. Pack samples/Hello.Static and build samples/consumer, which references it by version.
  5. Incremental builds: a rebuild without changes runs no tool; touching a public include
     reassembles exactly the sources that can include it.
  6. Run the executables (natively on Linux, through WSL on Windows when available) and the
     .NET sample that P/Invokes the shared libraries (dotnet on Linux, the .NET runtime
     container elsewhere when Docker is available).
"""

from __future__ import annotations

import argparse
import os
import platform
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SAMPLES = REPO / "samples"
ARTIFACTS = REPO / "artifacts"
FAILURES: list[str] = []


def check(what: str, ok: bool, detail: str = "") -> bool:
    """Record and print one check.

    Args:
        what: Description of the expectation.
        ok: Whether it held.
        detail: Extra text printed after a failure (the evidence).

    Returns:
        ``ok``, so callers can chain on it.
    """
    print(f"{'PASS' if ok else 'FAIL'} {what}" + (f"\n     {detail}" if not ok and detail else ""))
    if not ok:
        FAILURES.append(what)
    return ok


def host_rid() -> str:
    """Return the portable RID of this machine (win-x64, linux-x64, osx-arm64, ...).

    Returns:
        The RID string matching the toolchain package names.
    """
    system = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}[platform.system()]
    machine = platform.machine().lower()
    arch = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}[machine]
    return f"{system}-{arch}"


def run(args: list[str], env: dict | None = None, cwd: Path | None = None) -> subprocess.CompletedProcess:
    """Run a command, capturing combined output as text.

    Args:
        args: Command and arguments.
        env: Environment (defaults to the current one).
        cwd: Working directory (defaults to the repository root).

    Returns:
        The completed process (never raises on a non-zero exit; callers check).
    """
    return subprocess.run(args, cwd=cwd or REPO, env=env, text=True, capture_output=True, errors="replace")


def dotnet(args: list[str], env: dict) -> subprocess.CompletedProcess:
    """Run a dotnet command without MSBuild node reuse.

    Node reuse is off because a long-lived MSBuild node caches SDK files: an SDK repacked
    under the same version would otherwise go unnoticed between iterations.

    Args:
        args: Arguments after ``dotnet``.
        env: Environment, including the isolated NUGET_PACKAGES.

    Returns:
        The completed process.
    """
    return run(["dotnet", *args, "-nologo", "-nodeReuse:false"], env=env)


def readelf(tools: Path, path: Path, *options: str) -> str:
    """Return GNU readelf-style output of the bundled llvm-readobj for ``path``.

    Args:
        tools: The staged toolchain folder of this machine.
        path: ELF file or archive to inspect.
        *options: readelf options such as -h, -d, -l, --dyn-syms, -s.

    Returns:
        The tool's standard output (an empty string if it failed).
    """
    exe = tools / ("llvm-readobj.exe" if os.name == "nt" else "llvm-readobj")
    result = run([str(exe), "--elf-output-style=GNU", *options, str(path)])
    return result.stdout


def main() -> int:
    """Run every step and print a summary.

    Returns:
        0 when every check passed, 1 otherwise.

    Raises:
        SystemExit: If this machine's toolchain was not staged (the test cannot build).
    """
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    parser.add_argument("--run", default="auto", choices=["auto", "native", "wsl", "none"],
                        help="how to run the Linux executables (auto: natively on Linux, WSL on Windows)")
    parser.add_argument("--dotnet-run", default="auto", choices=["auto", "native", "docker", "none"],
                        help="how to run the .NET sample (auto: dotnet on Linux, Docker elsewhere)")
    args = parser.parse_args()

    rid = host_rid()
    # artifacts/toolchain/<rid>/tools/VERSIONS.txt -> <rid>
    staged = sorted(p.parent.parent.name for p in (ARTIFACTS / "toolchain").glob("*/tools/VERSIONS.txt"))
    if rid not in staged:
        raise SystemExit(f"No staged toolchain for {rid}. Run: python toolchain/fetch.py --rid {rid}")
    tools = ARTIFACTS / "toolchain" / rid / "tools"
    if os.name != "nt":
        # CI artifacts (and NuGet) drop the execute bit; llvm-readobj is called directly below.
        for tool in tools.iterdir():
            if not tool.name.endswith(".txt"):
                tool.chmod(0o755)

    env = dict(os.environ)
    env["NUGET_PACKAGES"] = str(ARTIFACTS / "test-nuget")
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    shutil.rmtree(ARTIFACTS / "test-nuget", ignore_errors=True)
    for stale in ARTIFACTS.glob("packages/Hello.*.nupkg"):
        stale.unlink()
    for project in SAMPLES.rglob("*proj"):
        for folder in ("bin", "obj"):
            shutil.rmtree(project.parent / folder, ignore_errors=True)

    # 1. Packages under test.
    result = dotnet(["pack", "src/AsmFoundation.Sdk/AsmFoundation.Sdk.csproj", "-c", "Release"], env)
    check("pack AsmFoundation.Sdk", result.returncode == 0, result.stdout[-2000:])
    for staged_rid in staged:
        result = dotnet(["pack", "toolchain/AsmFoundation.Toolchain.csproj", "-c", "Release",
                         f"-p:ToolchainRid={staged_rid}"], env)
        check(f"pack AsmFoundation.Toolchain.{staged_rid}", result.returncode == 0, result.stdout[-2000:])

    # 2. The sample solution.
    result = dotnet(["build", "samples/Samples.slnx", "-c", "Release"], env)
    built = check("build samples/Samples.slnx (bundled toolchain, TreatWarningsAsErrors)",
                  result.returncode == 0 and " 0 Warning(s)" in result.stdout, result.stdout[-3000:])
    if not built:
        return 1

    # 3. ELF properties.
    lib = SAMPLES / "Hello.Library/bin/Release/libhello.so"
    libc_lib = SAMPLES / "Hello.LibcLibrary/bin/Release/libhellolibc.so"
    app = SAMPLES / "Hello.App/bin/Release/hello-app"
    libc_app = SAMPLES / "Hello.Libc/bin/Release/hello-libc"
    archive = SAMPLES / "Hello.Static/bin/Release/libhellostatic.a"

    text = readelf(tools, lib, "-h", "-d", "--dyn-syms")
    check("libhello.so is a shared object", "DYN (Shared object file)" in text)
    check("libhello.so has SONAME libhello.so", "Library soname: [libhello.so]" in text)
    check("libhello.so needs nothing (freestanding)", "(NEEDED)" not in text)
    check("libhello.so exports hello_add and hello_version",
          bool(re.search(r"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+hello_add\b", text))
          and bool(re.search(r"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+hello_version\b", text)), text)

    text = readelf(tools, libc_lib, "-d", "--dyn-syms")
    check("libhellolibc.so needs libc.so.6 (stub link)", "Shared library: [libc.so.6]" in text, text)
    check("libhellolibc.so imports strlen", bool(re.search(r"UND\s+strlen\b", text)), text)

    text = readelf(tools, app, "-h", "-l")
    check("hello-app is a static executable", "EXEC (Executable file)" in text and "INTERP" not in text, text)

    # llvm-readobj prints a PIE's type as "DYN (Shared object file)" (GNU readelf says
    # "Position-Independent Executable"); the PIE flag in DT_FLAGS_1 is what tells them apart.
    text = readelf(tools, libc_app, "-h", "-l", "-d")
    check("hello-libc is a PIE with glibc's loader",
          "DYN (" in text and bool(re.search(r"\(FLAGS_1\).*\bPIE\b", text))
          and "/lib64/ld-linux-x86-64.so.2" in text and "Shared library: [libc.so.6]" in text, text)

    text = readelf(tools, archive, "-s")
    check("libhellostatic.a holds static_mul", bool(re.search(r"GLOBAL\s+DEFAULT\s+\d+\s+static_mul\b", text)), text)

    net_out = SAMPLES / "Hello.Net/bin/Release/net10.0"
    check("Hello.Net received libhello.so and libhellolibc.so from its ProjectReferences",
          (net_out / "libhello.so").exists() and (net_out / "libhellolibc.so").exists())

    # 4. Consumption by version through a package.
    result = dotnet(["pack", "samples/Hello.Static/Hello.Static.asmproj", "-c", "Release"], env)
    check("pack Hello.Static", result.returncode == 0, result.stdout[-2000:])
    result = dotnet(["build", "samples/consumer/Hello.Consumer.asmproj", "-c", "Release"], env)
    check("build Hello.Consumer against the Hello.Static 1.0.0 package", result.returncode == 0, result.stdout[-3000:])
    consumer = SAMPLES / "consumer/bin/Release/hello-consumer"

    # 5. Incremental builds.
    result = dotnet(["build", "samples/Samples.slnx", "-c", "Release", "-v:n"], env)
    # Count tool invocations only (assemble, link, archive): on Linux and macOS the SDK also
    # runs "chmod +x" on the same tool paths before each build, which is not a tool run.
    tool_runs = len(re.findall(r'(nasm(\.exe)?" -f|lld(\.exe)?" -flavor|llvm-ar(\.exe)?" rcs)', result.stdout))
    check("a rebuild without changes runs no tool", result.returncode == 0 and tool_runs == 0, f"{tool_runs} tool runs")
    (SAMPLES / "Hello.Static/include/hello_static.inc").touch()
    result = dotnet(["build", "samples/Samples.slnx", "-c", "Release", "-v:n"], env)
    assembled = sorted(set(re.findall(r'samples/([A-Za-z.]+/\w+\.asm)"', result.stdout)))
    check("touching Hello.Static's public include reassembles exactly its includers",
          assembled == ["Hello.App/main.asm", "Hello.Static/mul.asm"], str(assembled))

    # 6. Run.
    mode = args.run
    if mode == "auto":
        mode = "native" if platform.system() == "Linux" else ("wsl" if shutil.which("wsl") else "none")
    if mode == "none":
        print("SKIP running the executables (no Linux here)")
    else:
        for exe, expected in ((app, "6 * 7 = 42"), (libc_app, "3 arguments"), (consumer, "Hello.Static package")):
            if mode == "native":
                exe.chmod(0o755)
                result = run([str(exe), "a", "b"])
            else:
                rel = exe.relative_to(REPO).as_posix()
                result = run(["wsl", "-e", "sh", "-c", f"chmod +x '{rel}' && './{rel}' a b"])
            check(f"run {exe.name}", result.returncode == 0 and expected in result.stdout,
                  f"exit {result.returncode}: {result.stdout}{result.stderr}")

    mode = args.dotnet_run
    if mode == "auto":
        mode = "native" if platform.system() == "Linux" else ("docker" if shutil.which("docker") else "none")
    if mode == "none":
        print("SKIP running Hello.Net (needs Linux or Docker)")
    else:
        if mode == "native":
            result = run(["dotnet", str(net_out / "Hello.Net.dll")])
        else:
            result = run(["docker", "run", "--rm", "-v", f"{net_out}:/app:ro", "-w", "/app",
                          "mcr.microsoft.com/dotnet/runtime:10.0", "dotnet", "Hello.Net.dll"])
        check("Hello.Net P/Invokes both asm libraries", result.returncode == 0 and "FAIL" not in result.stdout,
              f"exit {result.returncode}: {result.stdout}{result.stderr}")

    print(f"\n{len(FAILURES)} failure(s)" + (": " + "; ".join(FAILURES) if FAILURES else ""))
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
