using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// ImplicitUsings in an .asmproj, .NET's property for what every source gets without writing
/// it (on by default): the SDK's platform layer (asmsdk.inc and "default rel" for NASM,
/// asmsdk-gnu.inc for llvm-mc), on Linux the .note.GNU-stack section, and for NASM on every OS
/// an explicit "section .text", in front of every source. tests/projects/ImplicitUsings has
/// sources that are code only, built for all eight RIDs; ImplicitUsingsOff disables it and must
/// then fail on the same kind of code. Sectionless (a static library) and SectionlessProgram
/// have sources that name no section at all, linked for all eight RIDs: NASM's COFF output
/// writes a label before the first section directive as an undefined symbol.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// The samples and most other test projects still write the includes, "default rel" and the
/// note themselves, and build without a warning under TreatWarningsAsErrors: the layer's include
/// guards, a repeated "default rel" and a repeated identical section declaration change
/// nothing, so code written for 0.3.0 keeps building. A source that wants absolute addressing
/// writes "default abs", which wins (tests/projects/ImageFixups, whose ASM0080 needs it).
/// </remarks>
public sealed class ImplicitUsingsTests(SdkUnderTest sdk)
{
    /// <summary>The ARM64 RIDs: their .s sources are assembled through a generated wrapper.</summary>
    public static readonly TheoryData<string> Arm64Rids = ["win-arm64", "linux-arm64", "linux-musl-arm64", "osx-arm64"];

    /// <summary>The Linux RIDs: their objects must carry the .note.GNU-stack section.</summary>
    public static readonly TheoryData<string> LinuxRids = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"];

    /// <summary>Builds tests/projects/ImplicitUsings in Release (all eight RIDs) once, asserting success.</summary>
    private void BuildImplicitUsings()
    {
        CommandResult build = sdk.BuildTestProject("ImplicitUsings");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>
    /// Code-only sources build for every RID without a warning (TreatWarningsAsErrors: NASM 3
    /// warns about each memory operand of a file that names neither default), in the RID's
    /// object format, and FUNCTION exports both functions on every OS.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void Code_only_sources_build_and_export_on_every_rid(string rid)
    {
        BuildImplicitUsings();
        string library = Repo.TestProjectPath("ImplicitUsings", "bin", "Release", "freestanding", rid, CrossPlatformTests.LibraryFile(rid, "implicit"));
        Assert.True(File.Exists(library), $"{rid}: no {library}");
        Assert.Contains($"Format: {CrossPlatformTests.Format[rid]}", sdk.ReadObj(library, "--file-headers"));
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            string exports = sdk.ReadObj(library, "--coff-exports");
            Assert.Contains("Name: ib_add", exports);
            Assert.Contains("Name: ib_answer", exports);
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            string symbols = sdk.ReadObj(library, "--symbols");
            Assert.Matches(@"Name: _ib_add \(\d+\)\s+Extern", symbols);
            Assert.Matches(@"Name: _ib_answer \(\d+\)\s+Extern", symbols);
        }
        else
        {
            string dynamic = sdk.ReadObj(library, "--dyn-syms");
            Assert.Contains("ib_add", dynamic);
            Assert.Contains("ib_answer", dynamic);
        }
    }

    /// <summary>
    /// On Linux the objects of code-only sources carry the empty .note.GNU-stack section that
    /// marks their stack non-executable: a static library's objects are linked by its users'
    /// tools, and GNU ld grants an executable stack to an object without it. Checked on the
    /// objects, not the library: the SDK's own link passes -z noexecstack either way.
    /// </summary>
    /// <param name="rid">A Linux runtime identifier.</param>
    [Theory]
    [MemberData(nameof(LinuxRids))]
    public void Linux_objects_declare_a_non_executable_stack(string rid)
    {
        BuildImplicitUsings();
        string objectFile = Repo.TestProjectPath("ImplicitUsings", "obj", "Release", "freestanding", rid, "asm", "obj", "implicit.o");
        Assert.True(File.Exists(objectFile), $"{rid}: no {objectFile}");
        Assert.Contains(".note.GNU-stack", sdk.ReadElf(objectFile, "-S"));
    }

    /// <summary>
    /// llvm-mc has no pre-include option: each ARM64 source is assembled through a file the SDK
    /// writes in obj/, which includes the GNU-syntax layer and then the source.
    /// </summary>
    /// <param name="rid">An ARM64 runtime identifier.</param>
    [Theory]
    [MemberData(nameof(Arm64Rids))]
    public void Arm64_sources_are_assembled_through_a_wrapper(string rid)
    {
        BuildImplicitUsings();
        string wrapper = Repo.TestProjectPath("ImplicitUsings", "obj", "Release", "freestanding", rid, "asm", "implicit", "implicit.s");
        Assert.True(File.Exists(wrapper), $"{rid}: no {wrapper}");
        string[] lines = File.ReadAllLines(wrapper);
        Assert.Equal(".include \"asmsdk-gnu-implicit.inc\"", lines[0]);
        Assert.Matches(@"^\.include "".*/ImplicitUsings/implicit\.s""$", lines[1]);
    }

    /// <summary>
    /// The build of this machine's RID loads into this process and answers: ib_add, and
    /// ib_answer, whose load from read-only data is RIP-relative on x64 only through the
    /// pre-included "default rel" (an absolute address would not have linked into the library on
    /// Linux and macOS, nor been right in a Windows DLL loaded elsewhere than its base).
    /// </summary>
    [Fact]
    public void The_hosts_own_build_answers()
    {
        BuildImplicitUsings();
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        nint library = CrossPlatformTests.LoadCopy(Repo.TestProjectPath("ImplicitUsings", "bin", "Release", "freestanding", rid, CrossPlatformTests.LibraryFile(rid, "implicit")));
        unsafe
        {
            var add = (delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(library, "ib_add");
            var answer = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "ib_answer");
            Assert.Equal(42, add(40, 2));
            Assert.Equal(42, answer());
        }
    }

    /// <summary>
    /// The values ImplicitUsings takes, as .NET reads them: enable and true turn the platform
    /// layer on, so the code-only sources build; anything else, disable and false among them,
    /// turns it off, so they fail at their first FUNCTION. Built for linux-x64 alone (the global
    /// RuntimeIdentifiers), each value in a configuration of its own, so the main Release build's
    /// objects, which the other tests read, are never replaced or deleted.
    /// </summary>
    /// <param name="value">The ImplicitUsings value.</param>
    /// <param name="on">Whether the platform layer is in.</param>
    [Theory]
    [InlineData("enable", true)]
    [InlineData("true", true)]
    [InlineData("disable", false)]
    [InlineData("false", false)]
    public void ImplicitUsings_takes_dotnets_values(string value, bool on)
    {
        CommandResult build = sdk.Dotnet("build", Repo.TestProject("ImplicitUsings"), "-c", $"Implicit{value}",
            "-p:RuntimeIdentifiers=linux-x64", $"-p:ImplicitUsings={value}");
        if (on)
        {
            Assert.True(build.ExitCode == 0, build.Tail());
        }
        else
        {
            Assert.True(build.ExitCode != 0, $"ImplicitUsings={value} built: {build.Tail()}");
            Assert.Contains("instruction expected, found `FUNCTION", build.StdOut);
        }
    }

    /// <summary>
    /// A static library whose source names no section and includes nothing
    /// (tests/projects/Sectionless, written as the asmstatic template's mul.asm is) defines its
    /// HIDDEN_FUNCTION in the text section of every RID's library. Archiving alone proves
    /// nothing: an archiver takes undefined symbols as they come, and only a link finds out.
    /// Until ImplicitUsings ended with "section .text" on every OS (2026-11-23), NASM's COFF
    /// output wrote sl_mul as an undefined symbol in the win-x64 library.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void A_sectionless_static_library_defines_its_function_on_every_rid(string rid)
    {
        BuildSectionlessProgram();
        string library = Repo.TestProjectPath("Sectionless", "bin", "Release", "freestanding", rid,
            rid.StartsWith("win-", StringComparison.Ordinal) ? "sectionless.lib" : "libsectionless.a");
        Assert.True(File.Exists(library), $"{rid}: no {library}");
        AssertDefinedInText(sdk.ReadObj(library, "--symbols"), "sl_mul", $"{rid}, {library}");
    }

    /// <summary>
    /// A libc program whose source names no section and includes nothing
    /// (tests/projects/SectionlessProgram) links the Sectionless library for every RID: the build
    /// is most of the test, since lld stops at an undefined symbol. Each RID's program is there
    /// in its format, and its main is defined in the text section of main.o. A program's main
    /// takes no export directive on Windows, so before the fix nothing switched sections for it
    /// and lld failed with "undefined symbol: main" on win-x64.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void A_sectionless_program_links_on_every_rid(string rid)
    {
        BuildSectionlessProgram();
        string program = SectionlessProgramFile(rid);
        Assert.True(File.Exists(program), $"{rid}: no {program}");
        Assert.Contains($"Format: {CrossPlatformTests.Format[rid]}", sdk.ReadObj(program, "--file-headers"));
        string objectFile = Repo.TestProjectPath("SectionlessProgram", "obj", "Release", "libc", rid, "asm", "obj", "main.o");
        Assert.True(File.Exists(objectFile), $"{rid}: no {objectFile}");
        AssertDefinedInText(sdk.ReadObj(objectFile, "--symbols"), "main", $"{rid}, {objectFile}");
    }

    /// <summary>
    /// The section-less program of this machine's RID runs, and main's sl_mul(6, 7) from the
    /// section-less static library is its exit code: 42.
    /// </summary>
    [Fact]
    public void The_hosts_sectionless_program_runs()
    {
        BuildSectionlessProgram();
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no program for this machine ({Repo.HostRid})");
        }

        string program = SectionlessProgramFile(rid);
        if (!OperatingSystem.IsWindows())
        {
            // The execute bit is set here, as the other program tests do, so that this test
            // checks what the program does, not its file mode.
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
        }

        CommandResult run = Command.Run(program, [], Repo.Root);
        Assert.True(run.ExitCode == 42, $"{program} exited with {run.ExitCode}: {run.Tail()}");
    }

    /// <summary>
    /// With ImplicitUsings disabled, nothing names .text for a source, so the section rule must
    /// come from asmsdk.inc itself: a static library whose source includes asmsdk.inc and names
    /// no section (tests/projects/SectionlessRaw) defines its HIDDEN_FUNCTION in the text section
    /// of every RID's library. Each function macro starts with NASM's __SECT__ since 2026-11-23;
    /// before, raw_mul was an undefined symbol in the win-x64 library.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void Without_implicit_usings_a_sectionless_static_library_defines_its_function_on_every_rid(string rid)
    {
        BuildSectionlessRawProgram();
        string library = Repo.TestProjectPath("SectionlessRaw", "bin", "Release", "freestanding", rid,
            rid.StartsWith("win-", StringComparison.Ordinal) ? "sectionlessraw.lib" : "libsectionlessraw.a");
        Assert.True(File.Exists(library), $"{rid}: no {library}");
        AssertDefinedInText(sdk.ReadObj(library, "--symbols"), "raw_mul", $"{rid}, {library}");
    }

    /// <summary>
    /// With ImplicitUsings disabled, a libc program whose source includes asmsdk.inc and names no
    /// section before its functions (tests/projects/SectionlessRawProgram) links the SectionlessRaw
    /// library for every RID, and its main is defined in main.o's text section. On win-x64 its
    /// LOCAL_FUNCTION seven, which a pointer in .data refers to, is defined too: NASM wrote that
    /// label undefined as well, and lld stopped with "undefined symbol: seven".
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void Without_implicit_usings_a_sectionless_program_links_on_every_rid(string rid)
    {
        BuildSectionlessRawProgram();
        string program = SectionlessRawProgramFile(rid);
        Assert.True(File.Exists(program), $"{rid}: no {program}");
        Assert.Contains($"Format: {CrossPlatformTests.Format[rid]}", sdk.ReadObj(program, "--file-headers"));
        string objectFile = Repo.TestProjectPath("SectionlessRawProgram", "obj", "Release", "libc", rid, "asm", "obj", "main.o");
        Assert.True(File.Exists(objectFile), $"{rid}: no {objectFile}");
        string symbols = sdk.ReadObj(objectFile, "--symbols");
        AssertDefinedInText(symbols, "main", $"{rid}, {objectFile}");
        if (rid == "win-x64")
        {
            AssertDefinedInText(symbols, "seven", $"{rid}, {objectFile}");
        }
    }

    /// <summary>
    /// The ImplicitUsings-off program of this machine's RID runs: main returns raw_mul(6, seven())
    /// through the static library and the pointer in .data, so its exit code is 42.
    /// </summary>
    [Fact]
    public void The_hosts_sectionless_program_without_implicit_usings_runs()
    {
        BuildSectionlessRawProgram();
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no program for this machine ({Repo.HostRid})");
        }

        string program = SectionlessRawProgramFile(rid);
        if (!OperatingSystem.IsWindows())
        {
            // As in The_hosts_sectionless_program_runs: the test checks what the program does,
            // not its file mode.
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
        }

        CommandResult run = Command.Run(program, [], Repo.Root);
        Assert.True(run.ExitCode == 42, $"{program} exited with {run.ExitCode}: {run.Tail()}");
    }

    /// <summary>
    /// The sections that llvm-readobj's <c>--symbols</c> output places a symbol in: one entry
    /// per symbol block whose name is <paramref name="name"/> or Mach-O's <c>_name</c> (an
    /// archive lists every member's symbols, and an ARM64 Mach-O function also carries its
    /// plain name as a local label, so a name may appear more than once).
    /// </summary>
    /// <param name="symbols">llvm-readobj's output with <c>--symbols</c>, in its own (LLVM) style, for COFF, ELF or Mach-O.</param>
    /// <param name="name">The symbol's C name, without Mach-O's underscore.</param>
    /// <returns>
    /// The text after "Section:" of each matching block, trimmed: ".text (1)" (COFF), ".text (0x2)"
    /// (ELF), "__text (0x1)" (Mach-O) when the symbol is defined there; "IMAGE_SYM_UNDEFINED (0)",
    /// "Undefined (0x0)" or "(0x0)" when it is not; an empty string for a block without a
    /// Section line. Empty when no block names the symbol.
    /// </returns>
    internal static IReadOnlyList<string> SymbolSections(string symbols, string name)
    {
        var sections = new List<string>();
        // Each symbol is a "Symbol {" block; the first chunk is the file header (and, for an
        // archive, each member starts with a "File:" line, which sits in the previous chunk and
        // matters to no block's Name or Section).
        foreach (string block in symbols.Split("Symbol {").Skip(1))
        {
            // The block's own name comes first; ELF and Mach-O append the string-table offset
            // in parentheses, COFF does not.
            Match named = Regex.Match(block, @"^\s*Name: (?<name>\S+)", RegexOptions.Multiline);
            if (!named.Success || (named.Groups["name"].Value != name && named.Groups["name"].Value != "_" + name))
            {
                continue;
            }

            Match section = Regex.Match(block, @"^\s*Section:(?<section>.*)$", RegexOptions.Multiline);
            sections.Add(section.Success ? section.Groups["section"].Value.Trim() : "");
        }

        return sections;
    }

    /// <summary>
    /// Asserts that llvm-readobj's <c>--symbols</c> output names <paramref name="name"/> and puts
    /// every block of it in a text section (.text on COFF and ELF, __text on Mach-O): defined,
    /// in code.
    /// </summary>
    /// <param name="symbols">llvm-readobj's output with <c>--symbols</c>.</param>
    /// <param name="name">The symbol's C name, without Mach-O's underscore.</param>
    /// <param name="what">The RID and file, for the failure message.</param>
    /// <exception cref="Xunit.Sdk.XunitException">No block names the symbol, or one places it outside a text section (undefined among others).</exception>
    internal static void AssertDefinedInText(string symbols, string name, string what)
    {
        IReadOnlyList<string> sections = SymbolSections(symbols, name);
        Assert.True(sections.Count > 0, $"{what}: no symbol {name}");
        foreach (string section in sections)
        {
            Assert.True(Regex.IsMatch(section, @"^(\.text|__text) "), $"{what}: {name} is in \"{section}\", not a text section");
        }
    }

    /// <summary>Builds tests/projects/SectionlessProgram in Release (all eight RIDs, with Sectionless through its reference) once, asserting success.</summary>
    /// <exception cref="Xunit.Sdk.XunitException">The build failed; the message holds the end of its log (an "undefined symbol" from lld when the fix regressed).</exception>
    private void BuildSectionlessProgram()
    {
        CommandResult build = sdk.BuildTestProject("SectionlessProgram");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>SectionlessProgram's program for a RID: sectionless-program.exe on Windows, sectionless-program elsewhere.</summary>
    /// <param name="rid">The runtime identifier.</param>
    /// <returns>The program's full path (which exists only once the project is built).</returns>
    private static string SectionlessProgramFile(string rid) =>
        Repo.TestProjectPath("SectionlessProgram", "bin", "Release", "libc", rid,
            rid.StartsWith("win-", StringComparison.Ordinal) ? "sectionless-program.exe" : "sectionless-program");

    /// <summary>Builds tests/projects/SectionlessRawProgram in Release (all eight RIDs, with SectionlessRaw through its reference) once, asserting success.</summary>
    /// <exception cref="Xunit.Sdk.XunitException">The build failed; the message holds the end of its log (an "undefined symbol" from lld when asmsdk.inc's __SECT__ regressed).</exception>
    private void BuildSectionlessRawProgram()
    {
        CommandResult build = sdk.BuildTestProject("SectionlessRawProgram");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>SectionlessRawProgram's program for a RID: sectionless-raw-program.exe on Windows, sectionless-raw-program elsewhere.</summary>
    /// <param name="rid">The runtime identifier.</param>
    /// <returns>The program's full path (which exists only once the project is built).</returns>
    private static string SectionlessRawProgramFile(string rid) =>
        Repo.TestProjectPath("SectionlessRawProgram", "bin", "Release", "libc", rid,
            rid.StartsWith("win-", StringComparison.Ordinal) ? "sectionless-raw-program.exe" : "sectionless-raw-program");

    /// <summary>
    /// &lt;ImplicitUsings&gt;disable&lt;/ImplicitUsings&gt; gives raw NASM back: the same kind of
    /// code-only source fails at its first line, because FUNCTION is a macro NASM was never
    /// given, reported as an MSBuild error at that line.
    /// </summary>
    [Fact]
    public void Disabled_implicit_usings_give_no_platform_layer()
    {
        CommandResult build = sdk.BuildTestProject("ImplicitUsingsOff");
        Assert.True(build.ExitCode != 0, $"ImplicitUsingsOff built, but must fail: {build.Tail()}");
        Assert.Matches(@"bare\.asm\(3\): error : instruction expected, found `FUNCTION", build.StdOut);
    }
}
