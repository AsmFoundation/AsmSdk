using System.Runtime.InteropServices;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// ImplicitUsings in an .asmproj, .NET's property for what every source gets without writing
/// it (on by default): the SDK's platform layer (asmsdk.inc and "default rel" for NASM,
/// asmsdk-gnu.inc for llvm-mc) and, on Linux, the .note.GNU-stack section, in front of every
/// source. tests/projects/ImplicitUsings has sources that are code only, built for all eight
/// RIDs; ImplicitUsingsOff disables it and must then fail on the same kind of code.
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
