using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// .NET's warning switches reach NASM by warning name: NoWarn turns a NASM warning off,
/// WarningsAsErrors makes it an error, and WarningsNotAsErrors keeps it a warning under
/// TreatWarningsAsErrors, as they do for a compiler's codes in a .csproj. Only entries shaped
/// like NASM's names (lower case, hyphens) are passed on, so one list can hold .NET's codes too.
/// tests/projects/WarningSwitches has one NASM warning on purpose (label-orphan) and builds
/// under the test projects' TreatWarningsAsErrors.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// Each case is a build of the same project with other global properties, into the same obj/
/// folder: each must assemble again, which the assemble options file (an input of the assemble
/// step) makes it do, so the cases also prove that an option from the command line is not
/// served from an object assembled without it.
/// </remarks>
public sealed class WarningSwitchTests(SdkUnderTest sdk)
{
    /// <summary>NASM's text for the warning, the same as a warning and as an error.</summary>
    private const string OrphanText = "label `lonely_label' alone on a line without a colon might be in error";

    /// <summary>Builds WarningSwitches in Release with the given properties, once per list.</summary>
    /// <param name="properties">The -p: arguments.</param>
    /// <returns>The build's result.</returns>
    private CommandResult Build(params string[] properties) => sdk.BuildTestProject("WarningSwitches", properties);

    /// <summary>Under TreatWarningsAsErrors the NASM warning is an error at its line, and the build fails.</summary>
    [Fact]
    public void Under_TreatWarningsAsErrors_the_warning_is_an_error()
    {
        CommandResult build = Build();
        Assert.True(build.ExitCode != 0, $"WarningSwitches built, but must fail: {build.Tail()}");
        Assert.Contains($"orphan.asm(9): error label-orphan: {OrphanText}", build.StdOut);
    }

    /// <summary>Without TreatWarningsAsErrors it is a warning at its line, and the build succeeds.</summary>
    [Fact]
    public void Without_TreatWarningsAsErrors_it_is_a_warning()
    {
        CommandResult build = Build("-p:TreatWarningsAsErrors=false");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains($"orphan.asm(9): warning label-orphan: {OrphanText}", build.StdOut);
    }

    /// <summary>NoWarn with NASM's warning name turns it off: no message, a successful build.</summary>
    [Fact]
    public void NoWarn_turns_the_warning_off()
    {
        CommandResult build = Build("-p:NoWarn=label-orphan");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("lonely_label", build.StdOut);
    }

    /// <summary>
    /// A NoWarn list that also holds .NET's and NuGet's codes (CS1591, NU5128, 1701) passes only
    /// NASM's name on: the build succeeds, and NASM gets no switch for the other codes. Read from
    /// the assemble options file, which holds NASM's arguments whether or not this build ran
    /// NASM: after NoWarn=label-orphan alone, the options are the same, and the up-to-date object
    /// is kept (as it must be).
    /// </summary>
    [Fact]
    public void A_mixed_NoWarn_list_passes_only_nasm_names()
    {
        CommandResult build = Build("-p:NoWarn=\"CS1591;NU5128;label-orphan;1701\"");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("lonely_label", build.StdOut);
        string nasm = File.ReadAllLines(Repo.TestProjectPath("WarningSwitches", "obj", "Release", "freestanding", "linux-x64", "asm", "assemble-options.txt"))
            .Single(line => line.StartsWith("nasm: ", StringComparison.Ordinal));
        Assert.Contains(" -w-label-orphan", nasm);
        Assert.DoesNotContain("CS1591", nasm);
        Assert.DoesNotContain("NU5128", nasm);
        Assert.DoesNotContain("1701", nasm);
    }

    /// <summary>WarningsAsErrors makes the one warning an error without TreatWarningsAsErrors.</summary>
    [Fact]
    public void WarningsAsErrors_makes_it_an_error()
    {
        CommandResult build = Build("-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=label-orphan");
        Assert.True(build.ExitCode != 0, $"WarningSwitches built, but must fail: {build.Tail()}");
        Assert.Contains($"orphan.asm(9): error label-orphan: {OrphanText}", build.StdOut);
    }

    /// <summary>WarningsNotAsErrors keeps the one warning a warning under TreatWarningsAsErrors.</summary>
    [Fact]
    public void WarningsNotAsErrors_keeps_it_a_warning()
    {
        CommandResult build = Build("-p:WarningsNotAsErrors=label-orphan");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains($"orphan.asm(9): warning label-orphan: {OrphanText}", build.StdOut);
    }

    /// <summary>
    /// The command line's -nowarn takes the NASM warning's name, which is its MSBuild code: the
    /// warning is not listed and the build succeeds. (0.4.0 logged NASM's warnings without a
    /// code, so -nowarn could not name them.)
    /// </summary>
    [Fact]
    public void The_command_lines_nowarn_takes_the_warning_name()
    {
        CommandResult build = Build("-p:TreatWarningsAsErrors=false", "-nowarn:label-orphan");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("lonely_label", build.StdOut);
    }

    /// <summary>The command line's -warnaserror with the name makes that warning an error, and the build fails.</summary>
    [Fact]
    public void The_command_lines_warnaserror_takes_the_warning_name()
    {
        CommandResult build = Build("-p:TreatWarningsAsErrors=false", "-warnaserror:label-orphan");
        Assert.True(build.ExitCode != 0, $"WarningSwitches built, but must fail: {build.Tail()}");
        Assert.Contains($"orphan.asm(9): error label-orphan: {OrphanText}", build.StdOut);
    }

    /// <summary>
    /// -warnaserror makes every warning an error but the one -warnnotaserror names: NASM's
    /// warning stays a warning, and the build succeeds.
    /// </summary>
    [Fact]
    public void The_command_lines_warnnotaserror_keeps_the_named_warning()
    {
        CommandResult build = Build("-p:TreatWarningsAsErrors=false", "-warnaserror", "-warnnotaserror:label-orphan");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains($"orphan.asm(9): warning label-orphan: {OrphanText}", build.StdOut);
    }

    /// <summary>
    /// A warning raised inside a macro names, in the same message, where the macro is defined:
    /// NASM writes that as a line of its own after the warning, which the SDK folds into it, so
    /// an IDE's error list shows both in one entry (macro.asm, built only with WarningCase=Macro).
    /// </summary>
    [Fact]
    public void A_warning_from_a_macro_names_the_macros_definition()
    {
        CommandResult build = Build("-p:TreatWarningsAsErrors=false", "-p:WarningCase=Macro");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains("macro.asm(9): warning number-overflow: byte exceeds bounds (macro.asm(4): from macro `OVERFLOWING_BYTE' defined here)", build.StdOut);
    }
}
