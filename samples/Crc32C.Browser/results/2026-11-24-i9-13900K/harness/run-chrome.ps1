<#
.SYNOPSIS
One timed run of Crc32C.Browser in headless Chrome, on one performance core that nothing else shares.

.DESCRIPTION
Starts coreguard on the timed core's other hyperthread (High priority, power throttling off), then
runs bench-chrome.mjs, which serves the bundle, opens bench.html in a headless Chrome with a
throw-away profile, pins every Chrome process to the timed CPU (pin-chrome.ps1) once the .NET
runtime has started, and only then starts the benchmark. The driver (node) itself is not pinned:
it only polls the page. The guard is stopped when the run ends, whatever happens.

.PARAMETER Bundle
A copy of a published app bundle: bench-chrome.mjs copies bench.html into it.

.PARAMETER Guard
coreguard.exe, built from coreguard.cs in this folder.

.PARAMETER Mode
Crc32C.Browser's mode: bench (the default) or verify.

.PARAMETER Sibling
The timed core's other hyperthread, which coreguard holds (3 on an i9-13900K, whose CPU 2 is timed).

.EXAMPLE
powershell -File run-chrome.ps1 -Bundle C:\tmp\aot -Guard C:\tmp\coreguard\coreguard.exe
#>
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [Parameter(Mandatory)] [string] $Guard,
    [string] $Mode = "bench",
    [int] $Sibling = 3
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ChromeRunThrottling {
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint Version; public uint ControlMask; public uint StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr process, int informationClass, ref State state, uint size);
    public static bool Disable(IntPtr process) {
        var state = new State { Version = 1, ControlMask = 1, StateMask = 0 };
        return SetProcessInformation(process, 4, ref state, (uint)Marshal.SizeOf(typeof(State)));
    }
}
"@

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$guardProcess = Start-Process -FilePath $Guard -ArgumentList "spin 600" -NoNewWindow -PassThru
$guardProcess.ProcessorAffinity = [IntPtr](1 -shl $Sibling)
$guardProcess.PriorityClass = 'High'
$off = [ChromeRunThrottling]::Disable($guardProcess.Handle)
"(guard: coreguard pid $($guardProcess.Id) on CPU $Sibling, High priority, power throttling off: $off)"
try {
    & node (Join-Path $here "bench-chrome.mjs") $Bundle $Mode --pin
}
finally {
    if (-not $guardProcess.HasExited) { $guardProcess.Kill() }
}
