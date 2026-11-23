using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Incremental builds: a rebuild without changes runs no tool, touching a public include
/// reassembles exactly the sources that can include it, and an assembler option set outside the
/// project file assembles again.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// The first two tests rebuild samples/Samples.slnx in Release, which the fixture built. Their
/// order does not matter: the include test ends with a rebuild, so the tree is up to date after
/// it too. The option test builds tests/projects/RidNone in Debug, which no other test builds.
/// </remarks>
public sealed class IncrementalBuildTests(SdkUnderTest sdk)
{
    /// <summary>
    /// A tool run in a normal-verbosity log: an assemble (NASM, whose first argument is -Xvc, or
    /// llvm-mc), a link or an archive. On Linux and macOS the SDK also runs "chmod +x" on the
    /// same tool paths before each build, which is not a tool run, so each tool is matched with
    /// the argument that follows it. (Until 2026-11-23 NASM was matched as "nasm -f", which
    /// stopped matching when -Xvc came first: a NASM run went unseen.)
    /// </summary>
    internal static readonly Regex ToolRun = new(@"(nasm(\.exe)?"" -Xvc|llvm-mc(\.exe)?"" --triple|lld(\.exe)?"" -flavor|llvm-ar(\.exe)?"" rcs)");

    /// <summary>
    /// An assembler run, either assembler: what a build that must assemble again shows.
    /// </summary>
    private static readonly Regex AssemblerRun = new(@"(nasm(\.exe)?"" -Xvc|llvm-mc(\.exe)?"" --triple)");

    /// <summary>
    /// A source NASM assembled, from its command line: the object path names the project
    /// (samples/&lt;project&gt;/obj/...), and the source is the last argument, relative to the
    /// project folder (as AsmFoundation.Sdk passes it, so the objects do not record the checkout
    /// folder). Project names may hold digits (Crc32C), so a stray reassembly of one could not
    /// hide. Group 1 is the project, group 2 the source.
    /// </summary>
    private static readonly Regex Assembled = new(@"samples/([A-Za-z0-9.]+)/obj/[^""]*"" ""([^""]+\.asm)""");

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
        string[] assembled = Assembled.Matches(build.StdOut).Select(m => $"{m.Groups[1].Value}/{m.Groups[2].Value}")
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Hello.App/main.asm", "Hello.Static/mul.asm"], assembled);
    }

    /// <summary>
    /// An assembler option set outside the project file (here DefineConstants on the command
    /// line; a Directory.Build.props is the same case) assembles again, and only once: the SDK
    /// writes the options to obj/.../asm/assemble-options.txt, an input of the assemble step,
    /// as .NET's CoreCompileInputs.cache recompiles C#. Before, such a change reused the objects
    /// assembled with the old options. Going back to the project's own options assembles again
    /// too. RidNone builds for this machine's RID, so the assembler is NASM on x64 machines and
    /// llvm-mc on ARM64 ones.
    /// </summary>
    [Fact]
    public void An_option_set_outside_the_project_file_assembles_again()
    {
        CommandResult Build(params string[] arguments) =>
            sdk.Dotnet(["build", Repo.TestProject("RidNone"), "-c", "Debug", "-v:n", .. arguments]);

        CommandResult first = Build();
        Assert.True(first.ExitCode == 0, first.Tail());
        CommandResult unchanged = Build();
        Assert.True(unchanged.ExitCode == 0, unchanged.Tail());
        Assert.Empty(AssemblerRun.Matches(unchanged.StdOut).Select(m => m.Value));

        CommandResult defined = Build("-p:DefineConstants=OPTION_FROM_THE_COMMAND_LINE");
        Assert.True(defined.ExitCode == 0, defined.Tail());
        Assert.NotEmpty(AssemblerRun.Matches(defined.StdOut).Select(m => m.Value));
        Assert.Contains("OPTION_FROM_THE_COMMAND_LINE", defined.StdOut);

        CommandResult definedAgain = Build("-p:DefineConstants=OPTION_FROM_THE_COMMAND_LINE");
        Assert.True(definedAgain.ExitCode == 0, definedAgain.Tail());
        Assert.Empty(AssemblerRun.Matches(definedAgain.StdOut).Select(m => m.Value));

        CommandResult back = Build();
        Assert.True(back.ExitCode == 0, back.Tail());
        Assert.NotEmpty(AssemblerRun.Matches(back.StdOut).Select(m => m.Value));
        Assert.DoesNotContain("OPTION_FROM_THE_COMMAND_LINE", back.StdOut);
    }
}
