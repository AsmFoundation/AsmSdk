<#
.SYNOPSIS
One run of Crc32C.Bench's core-scaling mode (--cores): the C# loop and the assembly on one
performance core and on four at once, with no other process's thread on those cores.

.DESCRIPTION
Runs "dotnet Crc32C.Bench.dll --cores <counts> --cpus <cpus>" at High priority. The program pins
each of its timed threads to one of the given logical CPUs (one per performance core) and turns
Windows' power throttling (EcoQoS) off for itself. Meanwhile one coreguard per core holds that
core's other hyperthread at High priority with power throttling off, so that no other process's
thread shares a timed core; every guard is stopped when the run ends, whatever happens. The
program's report (Markdown) and raw counts (JSON) go to the Out folder as run<N>-cores.md and
run<N>-cores.json.

.PARAMETER Bench
Crc32C.Bench.dll, built in Release (bin/Release/net10.0).

.PARAMETER Guard
coreguard.exe, built from samples/Crc32C.Browser/results/2026-11-24-i9-13900K/harness/coreguard.cs
(copy it out of the repository first: dotnet build coreguard.cs -c Release -o <folder>).

.PARAMETER Out
The folder for the report and the raw counts.

.PARAMETER Run
The run's number, for the file names.

.PARAMETER Cores
The core counts to time, comma-separated.

.PARAMETER Cpus
The logical CPUs the timed threads are pinned to, one per performance core: each core's first
hyperthread ("coreguard topology" lists them; 2,4,6,8 are performance cores 1 to 4 on an
i9-13900K, leaving core 0 and the interrupts Windows sends it alone).

.PARAMETER Siblings
The same cores' other hyperthreads, which the guards hold, in the same order (3,5,7,9).

.PARAMETER Rounds
Paired rounds.

.PARAMETER Seconds
Each cell's timed window, in seconds.

.EXAMPLE
powershell -File run-cores.ps1 -Bench K:\src\samples\Crc32C.Bench\bin\Release\net10.0\Crc32C.Bench.dll -Guard C:\tmp\coreguard\coreguard.exe -Out C:\tmp\cores -Run 1
#>
param(
    [Parameter(Mandatory)] [string] $Bench,
    [Parameter(Mandatory)] [string] $Guard,
    [Parameter(Mandatory)] [string] $Out,
    [Parameter(Mandatory)] [int] $Run,
    [string] $Cores = "1,4",
    [string] $Cpus = "2,4,6,8",
    [string] $Siblings = "3,5,7,9",
    [int] $Rounds = 12,
    [double] $Seconds = 0.5
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class CoresRunThrottling {
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

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$report = Join-Path $Out "run$Run-cores.md"
$json = Join-Path $Out "run$Run-cores.json"
$err = Join-Path $Out "run$Run-cores.err"
$guards = @()
try {
    # One guard per timed core, on its other hyperthread: pause instructions leave the core's
    # execution resources to the timed thread while keeping every other thread off the core.
    foreach ($sibling in ($Siblings -split ',' | ForEach-Object { [int]$_ })) {
        $guardProcess = Start-Process -FilePath $Guard -ArgumentList "spin 3600" -NoNewWindow -PassThru
        $guardProcess.ProcessorAffinity = [IntPtr](1 -shl $sibling)
        $guardProcess.PriorityClass = 'High'
        $guardOff = [CoresRunThrottling]::Disable($guardProcess.Handle)
        "(guard: coreguard pid $($guardProcess.Id) on CPU $sibling, High priority, power throttling off: $guardOff)"
        $guards += $guardProcess
    }

    $arguments = "`"$Bench`" --cores $Cores --cpus $Cpus --rounds $Rounds --seconds $Seconds --json `"$json`""
    $process = Start-Process -FilePath dotnet -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $report -RedirectStandardError $err
    # High priority keeps the timed threads ahead of normal work that the scheduler might still
    # place on the pinned CPUs; the program turns its own power throttling off before timing.
    $process.PriorityClass = 'High'
    $process.WaitForExit()
    "(run $Run`: cores $Cores on CPUs $Cpus, High priority, exit $($process.ExitCode))"
    Get-Content $err
}
finally {
    foreach ($running in $guards) {
        if (-not $running.HasExited) { $running.Kill() }
    }
    if ((Test-Path $err) -and (Get-Item $err).Length -eq 0) { Remove-Item $err }
}
