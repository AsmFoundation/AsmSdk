#!/usr/bin/env python3
"""End-to-end test of AsmFoundation.Sdk: pack it, build the samples, check every output.

Usage:
    python tests/run.py [--run auto|native|wsl|none] [--dotnet-run auto|native|docker|none]

Steps (each one prints PASS/FAIL lines; the exit code is the number of failures, capped at 1):
  1. Pack AsmFoundation.Sdk, plus the toolchain package for every RID whose tools are staged
     in artifacts/toolchain/<rid>/tools (toolchain/fetch.py stages them). The package for
     this machine must be among them. The SDK version must match the samples' and the test
     projects' global.json.
  2. Build samples/Samples.slnx in Release, against a fresh NuGet cache in
     artifacts/test-nuget, so the packages under test are always the ones just packed (the
     version number does not change between iterations, a shared cache would keep the
     first copy).
  3. Check the ELF properties of every output with the bundled llvm-readobj: file type,
     SONAME, DT_NEEDED, interpreter, exported symbols, archive members, debug sections; the
     target framework symbols NASM received; which build of a multi-targeted library a .NET
     project received.
  4. Pack samples/Hello.Static (single target framework) and samples/Hello.Library (both),
     check the package layouts, and build samples/consumer, which references Hello.Static by
     version.
  5. The test projects in tests/projects: target framework compatibility (NU1201, NU1202),
     the removed 0.1.0 settings (ASM0100-ASM0108), an unknown target framework (ASM0003),
     StartupObject, ExportsFile and LinkerArg, DefineConstants and DebugSymbols, and a
     multi-targeted project referencing a single-targeted one.
  6. Incremental builds: a rebuild without changes runs no tool; touching a public include
     reassembles exactly the sources that can include it.
  7. Run the executables (natively on Linux, through WSL on Windows when available) and the
     .NET sample that P/Invokes the shared libraries (dotnet on Linux, the .NET runtime
     container elsewhere when Docker is available).
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SAMPLES = REPO / "samples"
TEST_PROJECTS = REPO / "tests" / "projects"
ARTIFACTS = REPO / "artifacts"
FAILURES: list[str] = []
# An MSBuild or NuGet warning line ("warning NU5128: ..."). dotnet pack prints no
# "0 Warning(s)" summary at its default verbosity, so pack checks look for the lines instead.
MSBUILD_WARNING = re.compile(r"warning [A-Z]+[0-9]+")


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

    Raises:
        KeyError: On an operating system or machine type with no toolchain package.
    """
    system = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}[platform.system()]
    machine = platform.machine().lower()
    arch = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}[machine]
    return f"{system}-{arch}"


def sdk_version() -> str:
    """Return the AsmFoundation.Sdk version that Directory.Build.props sets.

    Returns:
        The <Version> value, for example "0.2.0".

    Raises:
        SystemExit: If Directory.Build.props has no <Version> (nothing could be tested).
    """
    match = re.search(r"<Version>([^<]+)</Version>", (REPO / "Directory.Build.props").read_text(encoding="utf-8"))
    if not match:
        raise SystemExit("Directory.Build.props sets no <Version>")
    return match.group(1).strip()


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
        *options: readelf options such as -h, -d, -l, -S, --dyn-syms, -s.

    Returns:
        The tool's standard output (an empty string if it failed).
    """
    exe = tools / ("llvm-readobj.exe" if os.name == "nt" else "llvm-readobj")
    result = run([str(exe), "--elf-output-style=GNU", *options, str(path)])
    return result.stdout


def package_entries(nupkg: Path) -> set[str]:
    """List the files of a package, without NuGet's own bookkeeping entries.

    Args:
        nupkg: The .nupkg file.

    Returns:
        The entry names (forward slashes), or an empty set when the package is missing.
    """
    if not nupkg.exists():
        return set()
    with zipfile.ZipFile(nupkg) as archive:
        return {name for name in archive.namelist()
                if not name.startswith(("_rels/", "package/")) and name != "[Content_Types].xml"}


def symbol_value(symbols: str, name: str) -> int | None:
    """Find a symbol's value in readelf -s output.

    Args:
        symbols: GNU-style symbol table text.
        name: The symbol name.

    Returns:
        The value as an integer, or None when the symbol is absent.
    """
    match = re.search(rf"^\s*\d+:\s+([0-9a-fA-F]+)\s+\d+\s+\w+\s+\w+\s+\w+\s+\S+\s+{re.escape(name)}$", symbols, re.M)
    return int(match.group(1), 16) if match else None


def test_project(name: str) -> Path:
    """Return the project file of a test project in tests/projects.

    Args:
        name: The folder name, which is also the project name.

    Returns:
        tests/projects/<name>/<name>.asmproj.
    """
    return TEST_PROJECTS / name / f"{name}.asmproj"


def expect_failure(env: dict, name: str, codes: list[str], absent: list[str] | None = None) -> None:
    """Build a test project that must fail, and check the diagnostics it reports.

    Args:
        env: Environment with the isolated NUGET_PACKAGES.
        name: The test project (see :func:`test_project`).
        codes: Diagnostic codes or texts that must all appear in the output.
        absent: Texts that must not appear (evidence that the build went too far).
    """
    result = dotnet(["build", str(test_project(name)), "-c", "Release"], env)
    missing = [code for code in codes if code not in result.stdout]
    present = [text for text in (absent or []) if text in result.stdout]
    check(f"{name} fails with {', '.join(codes)}",
          result.returncode != 0 and not missing and not present,
          f"exit {result.returncode}, missing {missing}, unexpected {present}: {result.stdout[-2500:]}")


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
    shutil.rmtree(ARTIFACTS / "test-packages", ignore_errors=True)
    for stale in [*ARTIFACTS.glob("packages/Hello.*.nupkg"), *ARTIFACTS.glob("packages/Test.*.nupkg")]:
        stale.unlink()
    for project in [*SAMPLES.rglob("*proj"), *TEST_PROJECTS.rglob("*proj")]:
        for folder in ("bin", "obj"):
            shutil.rmtree(project.parent / folder, ignore_errors=True)

    # 1. Packages under test, and the version every consumer pins.
    version = sdk_version()
    for global_json in (SAMPLES / "global.json", TEST_PROJECTS / "global.json"):
        pinned = json.loads(global_json.read_text(encoding="utf-8"))["msbuild-sdks"]["AsmFoundation.Sdk"]
        check(f"{global_json.relative_to(REPO).as_posix()} pins AsmFoundation.Sdk {version}", pinned == version, pinned)
    result = dotnet(["pack", "src/AsmFoundation.Sdk/AsmFoundation.Sdk.csproj", "-c", "Release"], env)
    check("pack AsmFoundation.Sdk", result.returncode == 0, result.stdout[-2000:])
    for staged_rid in staged:
        result = dotnet(["pack", "toolchain/AsmFoundation.Toolchain.csproj", "-c", "Release",
                         f"-p:ToolchainRid={staged_rid}"], env)
        check(f"pack AsmFoundation.Toolchain.{staged_rid}", result.returncode == 0, result.stdout[-2000:])

    # 2. The sample solution. Normal verbosity: step 3 reads the NASM command lines.
    result = dotnet(["build", "samples/Samples.slnx", "-c", "Release", "-v:n"], env)
    built = check("build samples/Samples.slnx (bundled toolchain, TreatWarningsAsErrors)",
                  result.returncode == 0 and " 0 Warning(s)" in result.stdout, result.stdout[-3000:])
    if not built:
        return 1
    build_log = result.stdout

    # 3. ELF properties. Outputs go to bin/<Configuration>/<TargetFramework>/, as in .NET.
    lib = SAMPLES / "Hello.Library/bin/Release/freestanding/libhello.so"
    lib_libc = SAMPLES / "Hello.Library/bin/Release/libc/libhello.so"
    libc_lib = SAMPLES / "Hello.LibcLibrary/bin/Release/libc/libhellolibc.so"
    app = SAMPLES / "Hello.App/bin/Release/freestanding/hello-app"
    libc_app = SAMPLES / "Hello.Libc/bin/Release/libc/hello-libc"
    archive = SAMPLES / "Hello.Static/bin/Release/freestanding/libhellostatic.a"

    text = readelf(tools, lib, "-h", "-d", "--dyn-syms")
    check("libhello.so (freestanding) is a shared object", "DYN (Shared object file)" in text)
    check("libhello.so has SONAME libhello.so", "Library soname: [libhello.so]" in text)
    check("libhello.so (freestanding) needs nothing", "(NEEDED)" not in text)
    check("libhello.so exports hello_add, hello_version and hello_uses_libc",
          all(re.search(rf"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+{name}\b", text)
              for name in ("hello_add", "hello_version", "hello_uses_libc")), text)

    text = readelf(tools, lib_libc, "-d")
    check("libhello.so (libc build of the same project) needs libc.so.6", "Shared library: [libc.so.6]" in text, text)
    check("NASM got -DRELEASE -DFREESTANDING and -DLIBC for the two builds of Hello.Library",
          bool(re.search(r"-DRELEASE -DFREESTANDING -o \"[^\"]*Hello\.Library/obj/Release/freestanding/", build_log))
          and bool(re.search(r"-DRELEASE -DLIBC -o \"[^\"]*Hello\.Library/obj/Release/libc/", build_log)))

    text = readelf(tools, libc_lib, "-d", "--dyn-syms")
    check("libhellolibc.so needs libc.so.6 (stub link)", "Shared library: [libc.so.6]" in text, text)
    check("libhellolibc.so imports strlen", bool(re.search(r"UND\s+strlen\b", text)), text)

    text = readelf(tools, app, "-h", "-l", "-S")
    check("hello-app is a static executable", "EXEC (Executable file)" in text and "INTERP" not in text, text)
    check("hello-app (Release) has no DWARF", ".debug_info" not in text)

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
    check("Hello.Net received the libc build of the multi-targeted Hello.Library (NuGet's nearest framework)",
          (net_out / "libhello.so").exists() and (net_out / "libhello.so").read_bytes() == lib_libc.read_bytes())

    result = dotnet(["build", "samples/Hello.App/Hello.App.asmproj", "-c", "Debug"], env)
    text = readelf(tools, SAMPLES / "Hello.App/bin/Debug/freestanding/hello-app", "-S")
    check("hello-app (Debug) has DWARF (DebugSymbols=true from Microsoft.NET.Sdk)",
          result.returncode == 0 and ".debug_info" in text, result.stdout[-2000:])

    # 4. Packages: layouts, and consumption by version.
    result = dotnet(["pack", "samples/Hello.Static/Hello.Static.asmproj", "-c", "Release"], env)
    check("pack Hello.Static, without warnings", result.returncode == 0 and not MSBUILD_WARNING.search(result.stdout),
          result.stdout[-2000:])
    entries = package_entries(ARTIFACTS / "packages/Hello.Static.1.0.0.nupkg")
    expected = {"Hello.Static.nuspec", "include/hello_static.inc", "lib/netstandard1.0/_._",
                "build/netstandard1.0/Hello.Static.props", "buildTransitive/netstandard1.0/Hello.Static.props",
                "native/linux-x64/freestanding/libhellostatic.a"}
    check("Hello.Static package: one freestanding (netstandard1.0) layout", entries == expected, str(sorted(entries)))

    result = dotnet(["pack", "samples/Hello.Library/Hello.Library.asmproj", "-c", "Release",
                     "-o", str(ARTIFACTS / "test-packages")], env)
    check("pack Hello.Library (TargetFrameworks=freestanding;libc), without warnings",
          result.returncode == 0 and not MSBUILD_WARNING.search(result.stdout), result.stdout[-2000:])
    entries = package_entries(ARTIFACTS / "test-packages/hello.1.0.0.nupkg")
    expected = {"hello.nuspec", "include/hello.inc"}
    for framework in ("netstandard1.0", "netstandard2.0"):
        expected |= {f"lib/{framework}/_._", f"build/{framework}/hello.props", f"buildTransitive/{framework}/hello.props",
                     f"runtimes/linux-x64/nativeassets/{framework}/libhello.so"}
    check("Hello.Library package: include/ once, one build/, lib/ marker and .so per framework",
          entries == expected, str(sorted(entries)))

    result = dotnet(["build", "samples/consumer/Hello.Consumer.asmproj", "-c", "Release"], env)
    check("build Hello.Consumer against the Hello.Static 1.0.0 package", result.returncode == 0, result.stdout[-3000:])
    consumer = SAMPLES / "consumer/bin/Release/freestanding/hello-consumer"

    # 5. Test projects.
    expect_failure(env, "OldNames", [f"ASM01{n:02d}" for n in range(9)], absent=["OldNames was assembled"])
    expect_failure(env, "BadFramework", ["ASM0003"])
    expect_failure(env, "FreestandingRefLibc", ["NU1201"])

    result = dotnet(["build", str(test_project("MultiRefSingle")), "-c", "Release"], env)
    multi = TEST_PROJECTS / "MultiRefSingle/bin/Release"
    check("MultiRefSingle builds both target frameworks, each linking the freestanding Hello.Static",
          result.returncode == 0 and all(
              re.search(r"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+static_mul\b",
                        readelf(tools, multi / framework / "libmultisquare.so", "--dyn-syms"))
              for framework in ("freestanding", "libc")), result.stdout[-3000:])
    check("the libc inner build did not pass TargetFramework=libc to the single-target reference",
          not (SAMPLES / "Hello.Static/bin/Release/libc").exists())

    result = dotnet(["build", str(test_project("Entry")), "-c", "Release"], env)
    entry_exe = TEST_PROJECTS / "Entry/bin/Release/freestanding/entry"
    header = readelf(tools, entry_exe, "-h")
    match = re.search(r"Entry point address:\s+0x([0-9a-fA-F]+)", header)
    entry_symbol = symbol_value(readelf(tools, entry_exe, "-s"), "entry_here")
    check("StartupObject=entry_here: the ELF entry point is entry_here",
          result.returncode == 0 and match is not None and entry_symbol is not None
          and int(match.group(1), 16) == entry_symbol, f"{header} entry_here={entry_symbol}: {result.stdout[-2000:]}")

    result = dotnet(["build", str(test_project("Exports")), "-c", "Release"], env)
    text = readelf(tools, TEST_PROJECTS / "Exports/bin/Release/libc/libexports.so", "-d", "--dyn-syms")
    check("ExportsFile: only exported_fn is exported, as exported_fn@@EXPORTS_1.0",
          result.returncode == 0 and "exported_fn@@EXPORTS_1.0" in text and "internal_fn" not in text,
          text + result.stdout[-2000:])
    check("LinkerArg -Bsymbolic: DT_FLAGS has SYMBOLIC", bool(re.search(r"\(FLAGS\).*\bSYMBOLIC\b", text)), text)

    result = dotnet(["build", str(test_project("Defines")), "-c", "Release"], env)
    check("Defines: DefineConstants=ANSWER=42 keeps RELEASE and FREESTANDING, and no NETSTANDARD",
          result.returncode == 0, result.stdout[-3000:])
    text = readelf(tools, TEST_PROJECTS / "Defines/obj/Release/freestanding/asm/obj/defines.o", "-S")
    check("Defines: DebugSymbols=true puts DWARF in a Release object", ".debug_info" in text, text)

    result = dotnet(["pack", str(test_project("LibcPackage")), "-c", "Release"], env)
    check("pack Test.LibcOnly (a libc-only package)", result.returncode == 0, result.stdout[-2000:])
    expect_failure(env, "FreestandingConsumer", ["NU1202"])
    result = dotnet(["build", str(test_project("LibcConsumer")), "-c", "Release"], env)
    libc_consumer = TEST_PROJECTS / "LibcConsumer/bin/Release/libc/libc-consumer"
    text = readelf(tools, libc_consumer, "-d")
    check("LibcConsumer links the package's shared library (DT_NEEDED libtestlibconly.so)",
          result.returncode == 0 and "Shared library: [libtestlibconly.so]" in text, text + result.stdout[-2000:])

    # 6. Incremental builds.
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

    # 7. Run.
    mode = args.run
    if mode == "auto":
        mode = "native" if platform.system() == "Linux" else ("wsl" if shutil.which("wsl") else "none")
    if mode == "none":
        print("SKIP running the executables (no Linux here)")
    else:
        # (program, expected exit status, expected output, LD_LIBRARY_PATH relative to the repository)
        runs = [(app, 0, "6 * 7 = 42", ""), (libc_app, 0, "3 arguments", ""), (consumer, 0, "Hello.Static package", ""),
                (entry_exe, 42, "", ""),
                (libc_consumer, 11, "", "artifacts/test-nuget/test.libconly/1.0.0/runtimes/linux-x64/native")]
        for exe, status, expected, library_path in runs:
            rel = exe.relative_to(REPO).as_posix()
            if mode == "native":
                exe.chmod(0o755)
                run_env = dict(os.environ, LD_LIBRARY_PATH=library_path) if library_path else None
                result = run([str(exe), "a", "b"], env=run_env)
            else:
                prefix = f"LD_LIBRARY_PATH='{library_path}' " if library_path else ""
                result = run(["wsl", "-e", "sh", "-c", f"chmod +x '{rel}' && {prefix}'./{rel}' a b"])
            check(f"run {exe.name} (exit {status})", result.returncode == status and expected in result.stdout,
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
        check("Hello.Net P/Invokes both asm libraries, and loaded the libc build of libhello.so",
              result.returncode == 0 and "FAIL" not in result.stdout and "hello_uses_libc() == 1" in result.stdout,
              f"exit {result.returncode}: {result.stdout}{result.stderr}")

    print(f"\n{len(FAILURES)} failure(s)" + (": " + "; ".join(FAILURES) if FAILURES else ""))
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
