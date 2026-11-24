<#
.SYNOPSIS
One timed run of Crc32C.Browser in Node, on one performance core that nothing else shares.

.DESCRIPTION
Runs "node main.mjs <mode>" in a bundle folder, pinned to one performance-core logical CPU, at
High priority, with Windows' power throttling (EcoQoS) off, which otherwise lands a shell-started
process on efficiency cores at about half speed. coreguard holds the same core's other
hyperthread meanwhile, so that no other process's thread shares the core; it is stopped when the
run ends, whatever happens. Prints the program's output (without .NET's harmless MONO_WASM
symbol-map message).

.PARAMETER Bundle
The app bundle to run: a published bin/Release/net10.0/browser-wasm/AppBundle, copied.

.PARAMETER Guard
coreguard.exe, built from coreguard.cs in this folder.

.PARAMETER Mode
Crc32C.Browser's mode: bench (the default) or verify.

.PARAMETER Cpu
The logical CPU to time on: a performance core's first hyperthread (coreguard topology lists
them; 2 is P-core 1 on an i9-13900K).

.PARAMETER Sibling
The same core's other hyperthread, which coreguard holds (3 on an i9-13900K).

.EXAMPLE
powershell -File run-node.ps1 -Bundle C:\tmp\interpreter -Guard C:\tmp\coreguard\coreguard.exe
#>
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [Parameter(Mandatory)] [string] $Guard,
    [string] $Mode = "bench",
    [int] $Cpu = 2,
    [int] $Sibling = 3
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NodeRunThrottling {
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint Version; public uint ControlMask; public uint StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr process, int informationClass, ref State state, uint size);
    // ProcessPowerThrottling = 4; PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1, state 0 = never throttle.
    public static bool Disable(IntPtr process) {
        var state = new State { Version = 1, ControlMask = 1, StateMask = 0 };
        return SetProcessInformation(process, 4, ref state, (uint)Marshal.SizeOf(typeof(State)));
    }
}
"@

$guardProcess = Start-Process -FilePath $Guard -ArgumentList "spin 600" -NoNewWindow -PassThru
$guardProcess.ProcessorAffinity = [IntPtr](1 -shl $Sibling)
$guardProcess.PriorityClass = 'High'
$guardOff = [NodeRunThrottling]::Disable($guardProcess.Handle)
"(guard: coreguard pid $($guardProcess.Id) on CPU $Sibling, High priority, power throttling off: $guardOff)"
$out = Join-Path $env:TEMP "crc32c-node-$PID.out"
$err = Join-Path $env:TEMP "crc32c-node-$PID.err"
try {
    $process = Start-Process -FilePath node -ArgumentList "main.mjs $Mode" -WorkingDirectory $Bundle `
        -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $process.ProcessorAffinity = [IntPtr](1 -shl $Cpu)
    $process.PriorityClass = 'High'
    $off = [NodeRunThrottling]::Disable($process.Handle)
    $process.WaitForExit()
    "(pinned to CPU $Cpu, High priority, power throttling off: $off, exit $($process.ExitCode))"
    Get-Content $out
    # .NET's runtime reports in Node that it could not load its symbol map: harmless, left out.
    Get-Content $err | Where-Object { $_ -notmatch 'MONO_WASM' }
}
finally {
    if (-not $guardProcess.HasExited) { $guardProcess.Kill() }
    Remove-Item $out, $err -ErrorAction SilentlyContinue
}
