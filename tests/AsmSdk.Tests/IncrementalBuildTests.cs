using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Incremental builds: a rebuild without changes runs no tool, and touching a public include
/// reassembles exactly the sources that can include it.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// Both tests rebuild samples/Samples.slnx in Release, which the fixture built. Their order does
/// not matter: the include test ends with a rebuild, so the tree is up to date after it too.
/// </remarks>
public sealed class IncrementalBuildTests(SdkUnderTest sdk)
{
    /// <summary>
    /// A tool run in a normal-verbosity log: an assemble, a link or an archive. On Linux and
    /// macOS the SDK also runs "chmod +x" on the same tool paths before each build, which is
    /// not a tool run.
    /// </summary>
    private static readonly Regex ToolRun = new(@"(nasm(\.exe)?"" -f|lld(\.exe)?"" -flavor|llvm-ar(\.exe)?"" rcs)");

    /// <summary>
    /// A source NASM assembled, from its command line: samples/&lt;project&gt;/&lt;file&gt;.asm.
    /// Project names may hold digits (Crc32C), so a stray reassembly of one could not hide.
    /// </summary>
    private static readonly Regex Assembled = new(@"samples/([A-Za-z0-9.]+/\w+\.asm)""");

    /// <summary>A rebuild without changes runs no tool.</summary>
    [Fact]
    public void A_rebuild_without_changes_runs_no_tool()
    {
        Assert.True(sdk.SamplesBuild.ExitCode == 0, "the samples did not build: " + sdk.SamplesBuild.Tail());
        CommandResult build = sdk.Dotnet("build", "samples/Samples.slnx", "-c", "Release", "-v:n");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Empty(ToolRun.Matches(build.StdOut).Select(m => m.Value));
    }

    /// <summary>
    /// Touching Hello.Static's public include reassembles exactly the sources that can include
    /// it: its own mul.asm and Hello.App's main.asm (through the project reference).
    /// </summary>
    [Fact]
    public void Touching_a_public_include_reassembles_exactly_its_includers()
    {
        Assert.True(sdk.SamplesBuild.ExitCode == 0, "the samples did not build: " + sdk.SamplesBuild.Tail());
        File.SetLastWriteTimeUtc(Repo.Sample("Hello.Static", "include", "hello_static.inc"), DateTime.UtcNow);
        CommandResult build = sdk.Dotnet("build", "samples/Samples.slnx", "-c", "Release", "-v:n");
        Assert.True(build.ExitCode == 0, build.Tail());
        string[] assembled = Assembled.Matches(build.StdOut).Select(m => m.Groups[1].Value)
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Hello.App/main.asm", "Hello.Static/mul.asm"], assembled);
    }
}
