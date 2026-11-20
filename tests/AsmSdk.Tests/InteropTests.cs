using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The interop tour (tests/projects/Interop): every way C# calls an asm library, run on this
/// machine. QaMath (17 exports, NASM for the x64 RIDs and GNU syntax for the ARM64 ones, linking
/// the static QaHelpers) and QaLibc (on the C library, x64 RIDs only) are reached through the
/// class library Qa.Interop by LibraryImport (spans, UTF-8 strings, out parameters, bool, structs
/// by value and by reference, 16-byte struct returns, function-pointer callbacks,
/// SuppressGCTransition), classic DllImport (arrays, delegates, ref structs), NativeLibrary and
/// function pointers, a DllImport resolver, a SafeHandle over memory the asm library allocated,
/// threads and garbage collections during callbacks, and SIMD over large arrays. Each check
/// compares with a C# reference on fixed-seed random data.
/// </summary>
/// <remarks>
/// The program (Qa.App, asmqa) prints one PASS/FAIL line per check and exits with the number
/// of failures. On an ARM64 machine QaLibc is absent by design, and the program checks that
/// its absence is handled. Built portable, so this also proves the host's RID choice from
/// deps.json on every CI machine, natively: before this test, the ARM64 code ran only under
/// QEMU, where the process crashed at exit after the large-array checks (the manual QA of
/// 2026-11-20 could not tell an emulator fault from a real one).
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class InteropTests(SdkUnderTest sdk)
{
    /// <summary>The tour's program project.</summary>
    private static readonly string App = Repo.TestProjectPath("Interop", "managed", "Qa.App", "Qa.App.csproj");

    /// <summary>
    /// The tour passes on this machine: every check, and none failed (51 on x64, 48 on ARM64,
    /// where the three QaLibc calls are replaced by the check that it is absent).
    /// </summary>
    [Fact]
    public void The_interop_tour_passes_on_this_machine()
    {
        if (CrossPlatformTests.HostBuildRid() is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        CommandResult build = sdk.Once("build Interop", () => sdk.Dotnet("build", App, "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("Interop", "managed", "Qa.App", "bin", "Release", "net10.0", "asmqa.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Match summary = Regex.Match(run.StdOut, @"(\d+) passed, (\d+) failed");
        Assert.True(summary.Success, run.Output);
        Assert.Equal("0", summary.Groups[2].Value);
        Assert.True(int.Parse(summary.Groups[1].Value) >= 48, run.Output);
    }
}
