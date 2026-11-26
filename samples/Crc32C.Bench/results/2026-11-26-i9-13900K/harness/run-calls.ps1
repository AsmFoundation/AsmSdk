<#
.SYNOPSIS
One run of Crc32C.Bench's call benchmarks (CallBenchmarks), on one performance core that nothing
else shares.

.DESCRIPTION
Runs "dotnet Crc32C.Bench.dll --filter *CallBenchmarks*" (BenchmarkDotNet, in-process, so the
benchmark runs in the started process) pinned to one performance-core logical CPU, at High
priority, with Windows' power throttling (EcoQoS) off, which otherwise lands a shell-started
process on efficiency cores at about half speed. coreguard holds the same core's other
hyperthread meanwhile, so that no other process's thread shares the core; it is stopped when the
run ends, whatever happens. BenchmarkDotNet's reports go to the Artifacts folder.

.PARAMETER Bench
Crc32C.Bench.dll, built in Release (bin/Release/net10.0).

.PARAMETER Guard
coreguard.exe, built from samples/Crc32C.Browser/results/2026-11-24-i9-13900K/harness/coreguard.cs
(copy it out of the repository first: dotnet build coreguard.cs -c Release -o <folder>).

.PARAMETER Artifacts
The folder BenchmarkDotNet writes its reports into.

.PARAMETER Cpu
The logical CPU to time on: a performance core's first hyperthread (coreguard topology lists
them; 2 is P-core 1 on an i9-13900K).

.PARAMETER Sibling
The same core's other hyperthread, which coreguard holds (3 on an i9-13900K).

.EXAMPLE
powershell -File run-calls.ps1 -Bench K:\src\samples\Crc32C.Bench\bin\Release\net10.0\Crc32C.Bench.dll -Guard C:\tmp\coreguard\coreguard.exe -Artifacts C:\tmp\run1
#>
param(
    [Parameter(Mandatory)] [string] $Bench,
    [Parameter(Mandatory)] [string] $Guard,
    [Parameter(Mandatory)] [string] $Artifacts,
    [int] $Cpu = 2,
    [int] $Sibling = 3
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BenchRunThrottling {
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

$guardProcess = Start-Process -FilePath $Guard -ArgumentList "spin 1800" -NoNewWindow -PassThru
$guardProcess.ProcessorAffinity = [IntPtr](1 -shl $Sibling)
$guardProcess.PriorityClass = 'High'
$guardOff = [BenchRunThrottling]::Disable($guardProcess.Handle)
"(guard: coreguard pid $($guardProcess.Id) on CPU $Sibling, High priority, power throttling off: $guardOff)"
$out = Join-Path $env:TEMP "crc32c-bench-$PID.out"
$err = Join-Path $env:TEMP "crc32c-bench-$PID.err"
try {
    # BenchmarkDotNet's own filter syntax: one quoted glob, the call benchmarks only.
    $process = Start-Process -FilePath dotnet -ArgumentList "`"$Bench`" --filter *CallBenchmarks* --artifacts `"$Artifacts`"" `
        -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $process.ProcessorAffinity = [IntPtr](1 -shl $Cpu)
    $process.PriorityClass = 'High'
    $off = [BenchRunThrottling]::Disable($process.Handle)
    $process.WaitForExit()
    "(pinned to CPU $Cpu, High priority, power throttling off: $off, exit $($process.ExitCode))"
    Get-Content $out | Select-Object -Last 30
    Get-Content $err
}
finally {
    if (-not $guardProcess.HasExited) { $guardProcess.Kill() }
    Remove-Item $out, $err -ErrorAction SilentlyContinue
}
