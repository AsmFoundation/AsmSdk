using System.Globalization;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The test projects in tests/projects, one SDK rule each: target framework compatibility
/// (NU1201, NU1202), the replaced settings (ASM0100–ASM0112), an unknown target framework
/// (ASM0003), a Compile item that is not asm (ASM0025), StartupObject, ExportsFile and
/// LinkerArg, NativeLibrary, DefineConstants and DebugSymbols, a libc-only package, and a
/// multi-targeted project referencing a single-targeted one.
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
    /// Every setting replaced by a .NET name fails with the error that names the replacement
    /// (ASM0100–ASM0107 for 0.2.0's; after 0.3.0, ASM0109 for AsmLinkLibrary, ASM0110 for
    /// AsmSource, ASM0111 for AsmInclude, ASM0112 for EnableDefaultAsmItems), then the ASM0108
    /// summary, before anything is assembled. Each new name is shown with the old value.
    /// </summary>
    [Fact]
    public void The_removed_settings_fail_with_their_fixes() =>
        ExpectFailure("OldNames",
            [
                .. Enumerable.Range(0, 13).Select(n => $"ASM01{n.ToString("00", CultureInfo.InvariantCulture)}"),
                "<NativeLibrary Include=\"libold.a\" />",
                "<Compile Include=\"extra/old-source.asm\" />",
                "<None Include=\"../shared/old.inc\" />",
                "<EnableDefaultCompileItems>true</EnableDefaultCompileItems>",
            ],
            absent: ["OldNames was assembled"]);

    /// <summary>
    /// A Compile item that is not an asm source (a .c file) stops the build with ASM0025, which
    /// names the file, before anything is assembled: NASM would otherwise read the C file as
    /// NASM and fail with a message about NASM's syntax.
    /// </summary>
    [Fact]
    public void A_compile_item_that_is_not_asm_fails() =>
        ExpectFailure("ForeignCompile", ["ASM0025", "helper.c cannot be compiled by an .asmproj"],
            absent: ["ForeignCompile ->", "instruction expected"]);

    /// <summary>
    /// A package packed by AsmFoundation.Sdk 0.3.0 adds AsmLinkLibrary items through its props:
    /// the consumer fails with ASM0109 naming that package (to be packed again), not with the
    /// message for a project's own AsmLinkLibrary items, and links nothing without its library.
    /// </summary>
    [Fact]
    public void A_package_with_the_old_link_item_names_itself() =>
        ExpectFailure("StalePackageProps", ["ASM0109", "The package Test.StaleNative was packed by AsmFoundation.Sdk 0.3.0 or older"],
            absent: ["AsmLinkLibrary items were replaced"]);

    /// <summary>
    /// A Compile item that a target adds during the build, as a code generator might
    /// (tests/projects/GeneratedCompileItem adds obj/.../generated.cs before AsmPrepare), is
    /// never handed to an assembler: only .asm and .s Compile items are sources. The build
    /// succeeds (ASM0025 checks only what the project lists), and no command line names the file.
    /// </summary>
    [Fact]
    public void A_compile_item_added_during_the_build_is_not_assembled()
    {
        CommandResult build = sdk.BuildTestProject("GeneratedCompileItem", "-v:n");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("generated.cs", build.StdOut);
        Assert.DoesNotContain("ASM0025", build.StdOut);
        Assert.True(File.Exists(Repo.TestProjectPath("GeneratedCompileItem", "bin", "Release", "freestanding", "linux-x64", "libgeneratedcompile.so")),
            build.Tail());
    }

    /// <summary>
    /// A NativeLibrary item (NativeAOT's name for an extra link input) links a static library
    /// built elsewhere, by its path: libnativelink.so defines static_mul from the samples'
    /// libhellostatic.a, rather than leaving it undefined for the loader, and exports nl_square,
    /// which calls it.
    /// </summary>
    [Fact]
    public void A_NativeLibrary_item_is_linked()
    {
        Assert.True(sdk.SamplesBuild.ExitCode == 0, "the samples (Hello.Static's archive) did not build: " + sdk.SamplesBuild.Tail());
        CommandResult build = sdk.BuildTestProject("NativeLibraryLink");
        Assert.True(build.ExitCode == 0, build.Tail());
        string symbols = sdk.ReadElf(Repo.TestProjectPath("NativeLibraryLink", "bin", "Release", "freestanding", "linux-x64", "libnativelink.so"), "--dyn-syms");
        // Defined: a section index, not UND.
        Assert.Matches(new Regex(@"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+static_mul\b"), symbols);
        Assert.Matches(new Regex(@"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+nl_square\b"), symbols);
    }

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

    /// <summary>
    /// A libc project links a package's shared library: DT_NEEDED libtestlibconly.so, the library
    /// copied next to the program from the package's runtimes/ folder, and DT_RUNPATH $ORIGIN so
    /// that the loader finds it there.
    /// </summary>
    [Fact]
    public void A_libc_project_links_a_packages_shared_library()
    {
        CommandResult build = BuildLibcConsumer(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
        string folder = Repo.TestProjectPath("LibcConsumer", "bin", "Release", "libc", "linux-x64");
        string dynamic = sdk.ReadElf(Path.Combine(folder, "libc-consumer"), "-d");
        Assert.Contains("Shared library: [libtestlibconly.so]", dynamic);
        Assert.Contains("Library runpath: [$ORIGIN]", dynamic);
        Assert.True(File.Exists(Path.Combine(folder, "libtestlibconly.so")), $"no libtestlibconly.so next to the program: {string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName))}");
    }
}
