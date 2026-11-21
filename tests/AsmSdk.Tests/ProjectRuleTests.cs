using System.Globalization;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The test projects in tests/projects, one SDK rule each: target framework compatibility
/// (NU1201, NU1202), the removed 0.1 settings (ASM0100–ASM0108), an unknown target framework
/// (ASM0003), StartupObject, ExportsFile and LinkerArg, DefineConstants and DebugSymbols, a
/// libc-only package, and a multi-targeted project referencing a single-targeted one.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class ProjectRuleTests(SdkUnderTest sdk)
{
    /// <summary>Packs tests/projects/LibcPackage (Test.LibcOnly, a libc-only package) into the local feed, once.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The pack's result.</returns>
    internal static CommandResult PackLibcPackage(SdkUnderTest sdk) =>
        sdk.Once("pack LibcPackage", () => sdk.Dotnet("pack", Repo.TestProject("LibcPackage"), "-c", "Release"));

    /// <summary>Builds tests/projects/LibcConsumer (it needs the Test.LibcOnly package), once.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The build's result.</returns>
    internal static CommandResult BuildLibcConsumer(SdkUnderTest sdk)
    {
        PackLibcPackage(sdk);
        return sdk.BuildTestProject("LibcConsumer");
    }

    /// <summary>Builds a test project that must fail, and checks the diagnostics it reports.</summary>
    /// <param name="name">The test project.</param>
    /// <param name="codes">Diagnostic codes that must all appear in the output.</param>
    /// <param name="absent">Texts that must not appear: evidence that the build went too far.</param>
    private void ExpectFailure(string name, string[] codes, string[]? absent = null)
    {
        CommandResult build = sdk.BuildTestProject(name);
        Assert.True(build.ExitCode != 0, $"{name} built, but must fail: {build.Tail()}");
        foreach (string code in codes)
        {
            Assert.True(build.StdOut.Contains(code, StringComparison.Ordinal), $"{name}: no {code} in {build.Tail()}");
        }

        foreach (string text in absent ?? [])
        {
            Assert.False(build.StdOut.Contains(text, StringComparison.Ordinal), $"{name}: \"{text}\" in {build.Tail()}");
        }
    }

    /// <summary>
    /// Every setting 0.2.0 removed fails with the error that names its .NET replacement
    /// (ASM0100–ASM0108), before anything is assembled.
    /// </summary>
    [Fact]
    public void The_removed_settings_fail_with_their_fixes() =>
        ExpectFailure("OldNames", Enumerable.Range(0, 9).Select(n => $"ASM01{n.ToString("00", CultureInfo.InvariantCulture)}").ToArray(),
            absent: ["OldNames was assembled"]);

    /// <summary>A target framework other than freestanding or libc fails with ASM0003.</summary>
    [Fact]
    public void An_unknown_target_framework_fails() => ExpectFailure("BadFramework", ["ASM0003"]);

    /// <summary>A freestanding project cannot reference a libc project: NuGet refuses it (NU1201).</summary>
    [Fact]
    public void A_freestanding_project_cannot_reference_a_libc_project() =>
        ExpectFailure("FreestandingRefLibc", ["NU1201"]);

    /// <summary>
    /// A multi-targeted project builds both target frameworks, and each links the
    /// freestanding build of its single-targeted reference.
    /// </summary>
    /// <param name="framework">The target framework of the build to inspect.</param>
    [Theory]
    [InlineData("freestanding")]
    [InlineData("libc")]
    public void A_multi_targeted_project_links_its_single_targeted_reference(string framework)
    {
        CommandResult build = sdk.BuildTestProject("MultiRefSingle");
        Assert.True(build.ExitCode == 0, build.Tail());
        string library = Repo.TestProjectPath("MultiRefSingle", "bin", "Release", framework, "linux-x64", "libmultisquare.so");
        Assert.Matches(new Regex(@"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+static_mul\b"), sdk.ReadElf(library, "--dyn-syms"));
    }

    /// <summary>
    /// The libc inner build did not pass TargetFramework=libc on to the single-target
    /// reference (which has no libc build): Hello.Static has no bin/Release/libc folder.
    /// </summary>
    [Fact]
    public void The_libc_inner_build_does_not_pass_its_framework_to_a_single_target_reference()
    {
        CommandResult build = sdk.BuildTestProject("MultiRefSingle");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.False(Directory.Exists(Repo.Sample("Hello.Static", "bin", "Release", "libc")));
    }

    /// <summary>StartupObject names the entry symbol: the ELF entry point is entry_here.</summary>
    [Fact]
    public void StartupObject_sets_the_entry_point()
    {
        CommandResult build = sdk.BuildTestProject("Entry");
        Assert.True(build.ExitCode == 0, build.Tail());
        string executable = Repo.TestProjectPath("Entry", "bin", "Release", "freestanding", "linux-x64", "entry");
        Match header = Regex.Match(sdk.ReadElf(executable, "-h"), @"Entry point address:\s+0x([0-9a-fA-F]+)");
        Match symbol = Regex.Match(sdk.ReadElf(executable, "-s"),
            @"^\s*\d+:\s+([0-9a-fA-F]+)\s+\d+\s+\w+\s+\w+\s+\w+\s+\S+\s+entry_here$", RegexOptions.Multiline);
        Assert.True(header.Success && symbol.Success, "no entry point or no entry_here symbol");
        Assert.Equal(Convert.ToUInt64(symbol.Groups[1].Value, 16), Convert.ToUInt64(header.Groups[1].Value, 16));
    }

    /// <summary>ExportsFile: only exported_fn is exported, versioned exported_fn@@EXPORTS_1.0.</summary>
    [Fact]
    public void ExportsFile_exports_only_the_listed_symbols()
    {
        CommandResult build = sdk.BuildTestProject("Exports");
        Assert.True(build.ExitCode == 0, build.Tail());
        string text = sdk.ReadElf(Repo.TestProjectPath("Exports", "bin", "Release", "libc", "linux-x64", "libexports.so"), "--dyn-syms");
        Assert.Contains("exported_fn@@EXPORTS_1.0", text);
        Assert.DoesNotContain("internal_fn", text);
    }

    /// <summary>
    /// LinkerArg items reach the linker: -z nodelete sets NODELETE in DT_FLAGS_1, which the SDK
    /// never sets itself. SYMBOLIC in DT_FLAGS comes from the SDK's own -Bsymbolic (0.3.0).
    /// </summary>
    [Fact]
    public void LinkerArg_reaches_the_linker()
    {
        CommandResult build = sdk.BuildTestProject("Exports");
        Assert.True(build.ExitCode == 0, build.Tail());
        string text = sdk.ReadElf(Repo.TestProjectPath("Exports", "bin", "Release", "libc", "linux-x64", "libexports.so"), "-d");
        Assert.Matches(new Regex(@"\(FLAGS_1\).*\bNODELETE\b"), text);
        // The SDK links every Linux shared library -Bsymbolic itself (0.3.0).
        Assert.Matches(new Regex(@"\(FLAGS\).*\bSYMBOLIC\b"), text);
    }

    /// <summary>
    /// DefineConstants=ANSWER=42 keeps the implicit RELEASE and FREESTANDING and passes no
    /// NETSTANDARD symbol (the project's own %ifdef checks fail the build otherwise).
    /// </summary>
    [Fact]
    public void DefineConstants_keeps_the_implicit_symbols()
    {
        CommandResult build = sdk.BuildTestProject("Defines");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>DebugSymbols=true puts DWARF into a Release object.</summary>
    [Fact]
    public void DebugSymbols_puts_DWARF_in_a_Release_object()
    {
        CommandResult build = sdk.BuildTestProject("Defines");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains(".debug_info", sdk.ReadElf(Repo.TestProjectPath("Defines", "obj", "Release", "freestanding", "linux-x64", "asm", "obj", "defines.o"), "-S"));
    }

    /// <summary>A libc-only package (Test.LibcOnly) packs.</summary>
    [Fact]
    public void A_libc_only_package_packs()
    {
        CommandResult pack = PackLibcPackage(sdk);
        Assert.True(pack.ExitCode == 0, pack.Tail());
    }

    /// <summary>A freestanding project cannot use a libc-only package: NuGet refuses it (NU1202).</summary>
    [Fact]
    public void A_freestanding_project_cannot_use_a_libc_only_package()
    {
        PackLibcPackage(sdk);
        ExpectFailure("FreestandingConsumer", ["NU1202"]);
    }

    /// <summary>A libc project links a package's shared library: DT_NEEDED libtestlibconly.so.</summary>
    [Fact]
    public void A_libc_project_links_a_packages_shared_library()
    {
        CommandResult build = BuildLibcConsumer(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains("Shared library: [libtestlibconly.so]",
            sdk.ReadElf(Repo.TestProjectPath("LibcConsumer", "bin", "Release", "libc", "linux-x64", "libc-consumer"), "-d"));
    }
}
