using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Incremental builds: a rebuild without changes runs no tool, touching a public include
/// reassembles exactly the sources that can include it, include files from outside the project
/// and extra inputs are dependencies while other files are not, and an assembler option set
/// outside the project file assembles again.
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
    /// What the assemble step depends on besides its sources, as .NET names it
    /// (tests/projects/DependencyInputs): a None item ending in .inc, here from a folder outside
    /// the project, and a CustomAdditionalCompileInputs item (a %incbin blob). Touching either
    /// assembles again; touching another None item (readme.txt) does not. The project also takes
    /// a source out with Compile Remove: excluded.asm is not assembly, so the first build
    /// succeeds only if NASM never sees it.
    /// </summary>
    [Fact]
    public void Include_files_and_extra_inputs_are_dependencies_and_other_files_are_not()
    {
        CommandResult Build() => sdk.Dotnet("build", Repo.TestProject("DependencyInputs"), "-c", "Debug", "-v:n");

        // Each touched file is dated now: after every object, which the previous build wrote, and
        // before the next build's. Not in the future: a file dated after the next build's objects
        // stays newer than them, and the build after that would assemble again for it (measured:
        // a blob dated now + 2 s made the readme step assemble).
        void Touch(params string[] parts) => File.SetLastWriteTimeUtc(Repo.TestProjectPath(parts), DateTime.UtcNow);

        CommandResult first = Build();
        Assert.True(first.ExitCode == 0, first.Tail());
        Assert.NotEmpty(AssemblerRun.Matches(first.StdOut).Select(m => m.Value));
        Assert.DoesNotContain("excluded.asm\"", first.StdOut);

        CommandResult unchanged = Build();
        Assert.True(unchanged.ExitCode == 0, unchanged.Tail());
        Assert.Empty(AssemblerRun.Matches(unchanged.StdOut).Select(m => m.Value));

        Touch("SharedIncludes", "outside.inc");
        CommandResult afterInclude = Build();
        Assert.True(afterInclude.ExitCode == 0, afterInclude.Tail());
        Assert.NotEmpty(AssemblerRun.Matches(afterInclude.StdOut).Select(m => m.Value));

        Touch("DependencyInputs", "blob.bin");
        CommandResult afterBlob = Build();
        Assert.True(afterBlob.ExitCode == 0, afterBlob.Tail());
        Assert.NotEmpty(AssemblerRun.Matches(afterBlob.StdOut).Select(m => m.Value));

        Touch("DependencyInputs", "readme.txt");
        CommandResult afterReadme = Build();
        Assert.True(afterReadme.ExitCode == 0, afterReadme.Tail());
        Assert.Empty(AssemblerRun.Matches(afterReadme.StdOut).Select(m => m.Value));
    }

    /// <summary>
    /// An assembler option set outside the project file (here DefineConstants on the command
    /// line; a Directory.Build.props is the same case) assembles again, and only once: the SDK
    /// writes the options to obj/.../asm/assemble-options.txt, an input of the assemble step,
    /// as .NET's CoreCompileInputs.cache recompiles C#. Before, such a change reused the objects
    /// assembled with the old options. Going back to the project's own options assembles again
    /// too. RidNone builds for this machine's RID, so the assembler is NASM on x64 machines and
    /// llvm-mc on ARM64 ones. The SDK's own symbols survive the command line's DefineConstants.
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
        // A DefineConstants from the command line is a global property, which ignores the SDK's
        // append of its own symbols: they are still passed (-D for NASM, --defsym for llvm-mc).
        // Without them, asmsdk-gnu.inc took the ELF branch on Windows on Arm and the build failed
        // (CI, 2026-11-23).
        Assert.Matches(@"(-D|--defsym=)DEBUG\b", defined.StdOut);
        Assert.Matches(@"(-D|--defsym=)(WINDOWS|LINUX|MACOS)\b", defined.StdOut);
        Assert.Matches(@"(-D|--defsym=)(X64|ARM64)\b", defined.StdOut);

        CommandResult definedAgain = Build("-p:DefineConstants=OPTION_FROM_THE_COMMAND_LINE");
        Assert.True(definedAgain.ExitCode == 0, definedAgain.Tail());
        Assert.Empty(AssemblerRun.Matches(definedAgain.StdOut).Select(m => m.Value));

        CommandResult back = Build();
        Assert.True(back.ExitCode == 0, back.Tail());
        Assert.NotEmpty(AssemblerRun.Matches(back.StdOut).Select(m => m.Value));
        Assert.DoesNotContain("OPTION_FROM_THE_COMMAND_LINE", back.StdOut);
    }
}
