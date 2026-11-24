#!/usr/bin/env bash
# run-all.sh - the whole measurement behind README.md: three complete runs of "bench" for each of
# the two published bundles (C# interpreted, C# AOT-compiled), in Node (run-node.ps1) and in
# headless Chrome (run-chrome.ps1), each pinned to CPU 2 with CPU 3 held by coreguard. Runs are
# interleaved (engine and bundle change every run), so that a slower stretch of the machine falls
# on every configuration, and the machine's load is sampled before each. Each run's output goes
# to <out>/run<N>-<engine>-<bundle>.txt; summarize.py then gives the tables. Git Bash on Windows.
#   run-all.sh <folder holding the bundles "interpreter" and "aot"> <coreguard.exe> <out folder>
set -u
here="$(cd "$(dirname "$0")" && pwd)"
bundles="$1"; guard="$2"; out="$3"
mkdir -p "$out"
load() { powershell -NoProfile -Command "(Get-CimInstance Win32_Processor).LoadPercentage" | tr -d '\r'; }
for run in 1 2 3; do
  for variant in interpreter aot; do
    file="$out/run$run-node-$variant.txt"
    { echo "machine load before: $(load)%"; date -u +%FT%TZ;
      powershell -NoProfile -ExecutionPolicy Bypass -File "$here/run-node.ps1" \
        -Bundle "$bundles/$variant" -Guard "$guard" -Mode bench; } > "$file" 2>&1
    file="$out/run$run-chrome-$variant.txt"
    { echo "machine load before: $(load)%"; date -u +%FT%TZ;
      powershell -NoProfile -ExecutionPolicy Bypass -File "$here/run-chrome.ps1" \
        -Bundle "$bundles/$variant" -Guard "$guard" -Mode bench; } > "$file" 2>&1
    # PowerShell writes CRLF: the outputs are kept with LF, like the rest of the repository.
    sed -i 's/\r$//' "$out"/run$run-*-$variant.txt
  done
done
