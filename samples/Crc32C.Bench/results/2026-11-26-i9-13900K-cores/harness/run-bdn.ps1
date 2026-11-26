<#
.SYNOPSIS
One BenchmarkDotNet run of Crc32C.Bench's Crc32CBenchmarks (the C# loop and the dispatcher), on
one performance core that nothing else shares: the cross-check for the core-scaling run.

.DESCRIPTION
Runs "dotnet Crc32C.Bench.dll --filter <filters>" (BenchmarkDotNet, in-process, so the
benchmark runs in the started process) pinned to one performance-core logical CPU, at High
priority, with Windows' power throttling (EcoQoS) off. coreguard holds the same core's other
hyperthread meanwhile, so no other process's thread shares the core; it is stopped when the run
ends, whatever happens. While the run goes, the timed CPU's actual clock is sampled every 5
seconds from Windows' processor counters (nominal frequency times "% Processor Performance") and
written next to BenchmarkDotNet's reports, so absolute figures can be read against the clock
they ran at.

.PARAMETER Bench
Crc32C.Bench.dll, built in Release (bin/Release/net10.0).

.PARAMETER Guard
coreguard.exe, built from samples/Crc32C.Browser/results/2026-11-24-i9-13900K/harness/coreguard.cs.

.PARAMETER Artifacts
The folder BenchmarkDotNet writes its reports into; clock.txt goes there too.

.PARAMETER Filter
BenchmarkDotNet's filters, space-separated globs.

.PARAMETER Cpu
The logical CPU to time on: a performance core's first hyperthread (2 is performance core 1 on
an i9-13900K).

.PARAMETER Sibling
The same core's other hyperthread, which coreguard holds (3 on an i9-13900K).
#>
param(
    [Parameter(Mandatory)] [string] $Bench,
    [Parameter(Mandatory)] [string] $Guard,
    [Parameter(Mandatory)] [string] $Artifacts,
    [string] $Filter = "*Crc32CBenchmarks.Managed* *Crc32CBenchmarks.Dispatcher*",
    [int] $Cpu = 2,
    [int] $Sibling = 3
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class BdnRunThrottling {
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

New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$guardProcess = Start-Process -FilePath $Guard -ArgumentList "spin 3600" -NoNewWindow -PassThru
$guardProcess.ProcessorAffinity = [IntPtr](1 -shl $Sibling)
$guardProcess.PriorityClass = 'High'
$guardOff = [BdnRunThrottling]::Disable($guardProcess.Handle)
"(guard: coreguard pid $($guardProcess.Id) on CPU $Sibling, High priority, power throttling off: $guardOff)"
$out = Join-Path $Artifacts "stdout.txt"
$err = Join-Path $Artifacts "stderr.txt"
$clock = Join-Path $Artifacts "clock.txt"
try {
    $globs = ($Filter -split ' ' | ForEach-Object { "`"$_`"" }) -join ' '
    $process = Start-Process -FilePath dotnet -ArgumentList "`"$Bench`" --filter $globs --artifacts `"$Artifacts`"" `
        -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $process.ProcessorAffinity = [IntPtr](1 -shl $Cpu)
    $process.PriorityClass = 'High'
    $off = [BdnRunThrottling]::Disable($process.Handle)
    # The timed CPU's clock: "Processor Frequency" is the nominal MHz, "% Processor Performance"
    # the share of it delivered (above 100 under turbo); their product is the actual clock.
    $instance = "0,$Cpu"
    "time,actual_mhz" | Set-Content $clock
    while (-not $process.HasExited) {
        $samples = (Get-Counter "\Processor Information($instance)\Processor Frequency", "\Processor Information($instance)\% Processor Performance" -ErrorAction SilentlyContinue).CounterSamples
        if ($samples.Count -eq 2) {
            $mhz = [math]::Round($samples[0].CookedValue * $samples[1].CookedValue / 100)
            "$(Get-Date -Format HH:mm:ss),$mhz" | Add-Content $clock
        }
        Start-Sleep -Seconds 5
    }
    "(pinned to CPU $Cpu, High priority, power throttling off: $off, exit $($process.ExitCode))"
    Get-Content $out | Select-Object -Last 25
    Get-Content $err
}
finally {
    if (-not $guardProcess.HasExited) { $guardProcess.Kill() }
}
