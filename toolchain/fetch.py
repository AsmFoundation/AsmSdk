#!/usr/bin/env python3
"""Stage the bundled toolchain (NASM, ld.lld, llvm-ar, llvm-readobj) for one build machine.

Usage:
    python toolchain/fetch.py --rid win-x64|linux-x64|osx-arm64
                              [--out artifacts/toolchain] [--cache artifacts/downloads]

Writes <out>/<rid>/tools/ with the executables, their license texts and VERSIONS.txt.
``dotnet pack toolchain/AsmFoundation.Toolchain.csproj -p:ToolchainRid=<rid>`` then turns
that folder into the AsmFoundation.Toolchain.<rid> package.

Where each tool comes from (pinned in versions.json, every download checked by SHA-256
before it is opened):
  - NASM on win-x64: the official win64 zip.
  - NASM elsewhere: built from the official source tarball on the machine running this
    script, which must therefore be the target OS (the release workflow runs one job per
    RID). The official macOS binary is i386/x86_64 only and would need Rosetta on Apple
    silicon; the Linux build is static so it runs on any distribution.
  - lld, llvm-ar, llvm-readobj: extracted from the official LLVM release archive for the
    RID. Only these files are read out of the 0.9-2 GB archive (streamed, never unpacked).

Downloads are cached by SHA-256 in --cache, so repeated runs (and CI with a cache) do not
fetch the large LLVM archives again.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
SUPPORTED_RIDS = ("win-x64", "linux-x64", "osx-arm64")


def sha256_of(path: Path) -> str:
    """Return the lowercase hex SHA-256 of a file.

    Args:
        path: File to hash (read in 1 MiB blocks, so multi-gigabyte archives are fine).

    Returns:
        The digest as 64 lowercase hex characters.

    Raises:
        OSError: If the file cannot be read.
    """
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url: str, sha256: str, cache: Path) -> Path:
    """Return a verified local copy of ``url``, downloading it only when not cached.

    The cache key is the expected hash, so a changed pin never reuses a stale file, and a
    corrupted or tampered cache entry is detected and replaced.

    Args:
        url: Source URL.
        sha256: Expected SHA-256 (lowercase hex) from versions.json.
        cache: Folder holding downloads, named by hash.

    Returns:
        Path of the verified file.

    Raises:
        SystemExit: If the downloaded file's hash differs from the pin (nothing is extracted
            from it, and it is deleted).
        urllib.error.URLError: If the download fails.
    """
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / f"{sha256}-{Path(url).name.replace('%2B', '+')}"
    if target.exists() and sha256_of(target) == sha256:
        print(f"cached   {target.name}")
        return target
    partial = target.with_suffix(target.suffix + ".part")
    print(f"download {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "AsmFoundation-toolchain-fetch"})
    with urllib.request.urlopen(request) as response, partial.open("wb") as out:
        shutil.copyfileobj(response, out, 1 << 20)
    actual = sha256_of(partial)
    if actual != sha256:
        partial.unlink()
        raise SystemExit(f"SHA-256 mismatch for {url}: expected {sha256}, got {actual}")
    partial.replace(target)
    return target


def make_executable(path: Path) -> None:
    """Set the owner/group/other execute bits on a file (no effect on Windows).

    Args:
        path: File to mark executable.
    """
    mode = path.stat().st_mode
    path.chmod(mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def stage_nasm(rid: str, pins: dict, cache: Path, tools: Path) -> None:
    """Put ``nasm`` (``nasm.exe`` on Windows) and NASM's LICENSE into ``tools``.

    Args:
        rid: Build-machine RID being staged.
        pins: The "nasm" object of versions.json.
        cache: Download cache folder.
        tools: Destination folder.

    Raises:
        SystemExit: If a download fails verification, or the source build fails, or the
            script runs on an OS that cannot build the requested RID.
        subprocess.CalledProcessError: If configure or make fails.
    """
    if rid == "win-x64":
        archive = download(pins["win-x64"]["url"], pins["win-x64"]["sha256"], cache)
        with zipfile.ZipFile(archive) as zf:
            for name in zf.namelist():
                base = name.rsplit("/", 1)[-1]
                if base in ("nasm.exe", "LICENSE"):
                    target = tools / ("LICENSE.nasm.txt" if base == "LICENSE" else base)
                    target.write_bytes(zf.read(name))
        return

    # Building from source needs the target OS: refuse early instead of producing a binary
    # for the wrong platform.
    expected = {"linux-x64": "linux", "osx-arm64": "darwin"}[rid]
    if sys.platform != expected:
        raise SystemExit(f"NASM for {rid} is built from source and must be staged on {expected}, not {sys.platform}")
    archive = download(pins["sources"]["url"], pins["sources"]["sha256"], cache)
    with tempfile.TemporaryDirectory() as tmp:
        with tarfile.open(archive, "r:xz") as tf:
            tf.extractall(tmp, filter="data")
        source = next(Path(tmp).iterdir())
        env = dict(os.environ)
        if rid == "linux-x64":
            # Static, so the binary runs on any distribution regardless of its glibc.
            env["LDFLAGS"] = "-static"
        subprocess.run(["./configure", "--disable-werror"], cwd=source, env=env, check=True,
                       stdout=subprocess.DEVNULL)
        subprocess.run(["make", "-j4", "nasm"], cwd=source, env=env, check=True,
                       stdout=subprocess.DEVNULL)
        shutil.copy2(source / "nasm", tools / "nasm")
        shutil.copy2(source / "LICENSE", tools / "LICENSE.nasm.txt")
    make_executable(tools / "nasm")


def stage_llvm(rid: str, pins: dict, cache: Path, tools: Path) -> None:
    """Extract the pinned LLVM tools for ``rid`` into ``tools``, plus LLVM's license.

    The archive is read as a stream, member by member; only ``bin/<tool>[.exe]`` entries are
    written. A wanted entry that is a symbolic link (release archives link ld.lld -> lld and
    similar) is resolved to the file it points to.

    Args:
        rid: Build-machine RID being staged (selects the archive).
        pins: The "llvm" object of versions.json.
        cache: Download cache folder.
        tools: Destination folder.

    Raises:
        SystemExit: If a download fails verification or a tool is missing from the archive.
    """
    exe = ".exe" if rid.startswith("win") else ""
    wanted = {f"{tool}{exe}" for tool in pins["tools"]}
    archive = download(pins[rid]["url"], pins[rid]["sha256"], cache)
    found: dict[str, bytes] = {}
    links: dict[str, str] = {}
    with tarfile.open(archive, "r|xz") as tf:
        for member in tf:
            parts = member.name.split("/")
            if len(parts) < 3 or parts[-2] != "bin":
                continue
            base = parts[-1]
            if member.issym() or member.islnk():
                links[base] = member.linkname.rsplit("/", 1)[-1]
                continue
            # Keep link targets too: a wanted name may turn out to be a link to this file.
            if base in wanted or base in links.values() or base.startswith(("lld", "llvm-ar", "llvm-readobj")):
                stream = tf.extractfile(member)
                if stream is not None:
                    found[base] = stream.read()
    for name in sorted(wanted):
        source = name
        seen = set()
        while source not in found and source in links and source not in seen:
            seen.add(source)
            source = links[source]
        if source not in found:
            raise SystemExit(f"{name} not found in {archive.name}")
        target = tools / name
        target.write_bytes(found[source])
        make_executable(target)
    license_file = download(pins["license"]["url"], pins["license"]["sha256"], cache)
    shutil.copy2(license_file, tools / "LICENSE.llvm.txt")


def main() -> int:
    """Parse arguments, stage NASM and LLVM for one RID, and write VERSIONS.txt.

    Returns:
        Process exit code (0 on success).

    Raises:
        SystemExit: On an unsupported RID or any failed verification or build step.
    """
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n", 1)[0])
    parser.add_argument("--rid", required=True, choices=SUPPORTED_RIDS)
    parser.add_argument("--out", default=str(REPO / "artifacts" / "toolchain"))
    parser.add_argument("--cache", default=str(REPO / "artifacts" / "downloads"))
    args = parser.parse_args()

    pins = json.loads((HERE / "versions.json").read_text(encoding="utf-8"))
    tools = Path(args.out) / args.rid / "tools"
    if tools.exists():
        shutil.rmtree(tools)
    tools.mkdir(parents=True)
    cache = Path(args.cache)

    stage_nasm(args.rid, pins["nasm"], cache, tools)
    stage_llvm(args.rid, pins["llvm"], cache, tools)
    (tools / "VERSIONS.txt").write_text(
        f"AsmFoundation.Toolchain.{args.rid} {pins['packageVersion']}\n"
        f"NASM {pins['nasm']['version']}\n"
        f"LLVM {pins['llvm']['version']} ({', '.join(pins['llvm']['tools'])})\n",
        encoding="utf-8")
    for entry in sorted(tools.iterdir()):
        print(f"{entry.stat().st_size / 1048576:8.1f} MB  {entry.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
