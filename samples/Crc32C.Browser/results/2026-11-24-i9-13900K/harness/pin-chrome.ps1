<#
.SYNOPSIS
Pins every process of one headless Chrome to one logical CPU, at High priority, without EcoQoS.

.DESCRIPTION
The browser twin of run-node.ps1's pinning. Chrome's processes are found by the --user-data-dir
in their command lines. bench-chrome.mjs runs this once the page has started the .NET runtime,
so that the renderer process exists, and before the benchmark starts: a renderer started later
(another site) would not be pinned. A process that ends between the listing and the pinning is
skipped. Prints how many processes it pinned.

.PARAMETER ProfileDir
The --user-data-dir the Chrome was started with (a throw-away folder per run).

.PARAMETER Cpu
The logical CPU to pin to (2: P-core 1's first hyperthread on an i9-13900K).
#>
param(
    [Parameter(Mandatory)] [string] $ProfileDir,
    [int] $Cpu = 2
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ChromeThrottling {
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

$needle = $ProfileDir.Replace('/', '\')
$pinned = 0
foreach ($entry in Get-CimInstance Win32_Process -Filter "Name = 'chrome.exe'") {
    if ($entry.CommandLine -and $entry.CommandLine.Replace('/', '\').Contains($needle)) {
        try {
            $process = Get-Process -Id $entry.ProcessId -ErrorAction Stop
            $process.ProcessorAffinity = [IntPtr](1 -shl $Cpu)
            $process.PriorityClass = 'High'
            [void][ChromeThrottling]::Disable($process.Handle)
            $pinned++
        } catch {
            # A process that ended between the listing and here (a utility process) is skipped.
        }
    }
}
"pinned $pinned chrome processes to CPU $Cpu"
