using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// How a failing build reports itself: an assembler's error as an MSBuild error at its file and
/// line (tests/projects/NasmError), and the System toolchain's linker limit as ASM0005 before
/// any tool runs (tests/projects/SystemLd). Both found by the manual interop QA of 2026-11-20.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class DiagnosticsTests(SdkUnderTest sdk)
{
    /// <summary>
    /// NASM's error is an MSBuild error with its file and line, "nasmerror.asm(7): error : ...",
    /// tagged with the project like every MSBuild error, so IDEs list it and open the line; then
    /// ASM0007 for the deleted object. Before -Xvc, NASM's "nasmerror.asm:7: error:" was plain
    /// output (MSBuild recognizes the GCC form only with a column), and the only errors were
    /// ASM0007 and MSB3073.
    /// </summary>
    [Fact]
    public void A_nasm_error_is_an_msbuild_error_at_its_line()
    {
        CommandResult build = sdk.BuildTestProject("NasmError");
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Matches(new Regex(@"nasmerror\.asm\(7\): error : instruction expected.*\[.*NasmError\.asmproj\]"), build.StdOut);
        Assert.Contains("ASM0007", build.StdOut);
    }

    /// <summary>
    /// AsmToolchain=System links Linux RIDs with the machine's GNU ld, which links ELF files of
    /// its own machine type only: the Linux RID of the other architecture stops with ASM0005,
    /// naming ld.lld, before any tool runs (so no NASM or llvm-mc is needed here, on any
    /// machine). It stopped with GNU ld's "file in wrong format" before (Ubuntu 24.04 x64,
    /// binutils 2.42).
    /// </summary>
    [Fact]
    public void A_linux_rid_of_another_architecture_with_gnu_ld_stops_with_ASM0005()
    {
        // The build machine's architecture as the .NET SDK reports it, which the check compares.
        string hostArch = Repo.HostRid[(Repo.HostRid.LastIndexOf('-') + 1)..];
        string foreign = hostArch == "arm64" ? "linux-x64" : "linux-arm64";
        CommandResult build = sdk.BuildTestProject("SystemLd", "-r", foreign);
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("ASM0005", build.StdOut);
        Assert.Contains($"which links {hostArch} ELF files only", build.StdOut);
        Assert.Contains("ld.lld", build.StdOut);
    }
}
