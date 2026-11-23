using System.IO.Compression;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Packages built from .asmproj projects: their layouts (one per target framework), their
/// dependencies (a static library linked into a shared library is none, by default), and
/// consumption by version from another project.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class PackageTests(SdkUnderTest sdk)
{
    /// <summary>
    /// An MSBuild or NuGet warning line ("warning NU5128: ..."). dotnet pack prints no
    /// "0 Warning(s)" summary at its default verbosity, so pack checks look for the lines.
    /// </summary>
    private static readonly Regex MsBuildWarning = new("warning [A-Z]+[0-9]+");

    /// <summary>Packs samples/Hello.Static into the local feed, once.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The pack's result.</returns>
    internal static CommandResult PackHelloStatic(SdkUnderTest sdk) =>
        sdk.Once("pack Hello.Static", () => sdk.Dotnet("pack", "samples/Hello.Static/Hello.Static.asmproj", "-c", "Release"));

    /// <summary>Builds samples/consumers/Hello.Consumer, which references the Hello.Static 1.0.0 package by version, once.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The build's result (after packing Hello.Static, which it needs).</returns>
    internal static CommandResult BuildConsumer(SdkUnderTest sdk)
    {
        PackHelloStatic(sdk);
        return sdk.Once("build consumer", () => sdk.Dotnet("build", "samples/consumers/Hello.Consumer/Hello.Consumer.asmproj", "-c", "Release"));
    }

    /// <summary>
    /// A single-target (freestanding) package: the public include files, an empty lib/ marker
    /// for netstandard1.0 (the framework behind freestanding), the consumer props, and the
    /// static library under native/.
    /// </summary>
    [Fact]
    public void A_single_target_package_has_one_freestanding_layout()
    {
        CommandResult pack = PackHelloStatic(sdk);
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                "Hello.Static.nuspec", "include/hello_static.inc", "lib/netstandard1.0/_._",
                "build/netstandard1.0/Hello.Static.props", "buildTransitive/netstandard1.0/Hello.Static.props",
                "native/linux-x64/freestanding/libhellostatic.a",
            },
            SdkUnderTest.PackageEntries(Path.Combine(Repo.Artifacts, "packages", "Hello.Static.1.0.0.nupkg")));
    }

    /// <summary>
    /// A multi-targeted package (TargetFrameworks=freestanding;libc): include/ once, and per
    /// framework a lib/ marker, the consumer props, the .NET consumer targets (BinariesCopyPath;
    /// NuGet imports the nearest framework's, the one whose library it also picks) and the shared
    /// library.
    /// </summary>
    [Fact]
    public void A_multi_targeted_package_has_one_layout_per_framework()
    {
        string output = Path.Combine(Repo.Artifacts, "test-packages");
        CommandResult pack = sdk.Once("pack Hello.Library",
            () => sdk.Dotnet("pack", "samples/Hello.Library/Hello.Library.asmproj", "-c", "Release", "-o", output));
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        var expected = new SortedSet<string>(StringComparer.Ordinal) { "hello.nuspec", "include/hello.inc" };
        foreach (string framework in new[] { "netstandard1.0", "netstandard2.0" })
        {
            expected.UnionWith([
                $"lib/{framework}/_._", $"build/{framework}/hello.props", $"buildTransitive/{framework}/hello.props",
                $"build/{framework}/hello.targets", $"buildTransitive/{framework}/hello.targets",
                $"runtimes/linux-x64/nativeassets/{framework}/libhello.so",
            ]);
        }

        Assert.Equal(expected, SdkUnderTest.PackageEntries(Path.Combine(output, "hello.1.0.0.nupkg")));
    }

    /// <summary>
    /// AsmPackSources=true (tests/projects/PackSources) packs the sources under src/, each at its
    /// path in the project: the Compile items (lib.asm, and lib.s although the package's one RID
    /// builds only the .asm) and the None items ending in .inc, public (include/pub.inc) or not
    /// (private.inc); not notes.txt, a None item that is no source. The public include file is
    /// also the package's include/, as without the property.
    /// </summary>
    [Fact]
    public void AsmPackSources_packs_the_sources_and_include_files()
    {
        string output = Path.Combine(Repo.Artifacts, "test-packages", "pack-sources");
        CommandResult pack = sdk.Once("pack PackSources", () => sdk.Dotnet("pack", Repo.TestProject("PackSources"), "-c", "Release", "-o", output));
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        SortedSet<string> entries = SdkUnderTest.PackageEntries(Path.Combine(output, "Test.PackSources.1.0.0.nupkg"));
        Assert.Equal(["src/include/pub.inc", "src/lib.asm", "src/lib.s", "src/private.inc"],
            entries.Where(entry => entry.StartsWith("src/", StringComparison.Ordinal)));
        Assert.Contains("include/pub.inc", entries);
    }

    /// <summary>
    /// A shared library's reference to a static library that sets no PrivateAssets is private by
    /// default (tests/projects/PrivateStatic -> Sectionless): its package does not depend on the
    /// static library, whose code is linked in and which has no package of its own (before
    /// 2026-11-23, every consumer of such a package failed with NU1101), and a .NET project that
    /// references the shared library (NetPrivateStatic) does not get the static library as a
    /// transitive reference: its assets file does not name it. In a configuration of its own
    /// (PrivateStatic), so Sectionless's Release builds stay as other tests left them.
    /// </summary>
    [Fact]
    public void A_shared_librarys_static_library_reference_is_private_by_default()
    {
        string output = Path.Combine(Repo.Artifacts, "test-packages", "private-static");
        CommandResult pack = sdk.Dotnet("pack", Repo.TestProject("PrivateStatic"), "-c", "PrivateStatic", "-o", output);
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        Assert.DoesNotContain("<dependency id=\"sectionless\"", Nuspec(Path.Combine(output, "Test.PrivateStatic.1.0.0.nupkg"), "Test.PrivateStatic.nuspec"));

        CommandResult restore = sdk.Dotnet("restore", Repo.TestProjectPath("NetPrivateStatic", "NetPrivateStatic.csproj"));
        Assert.True(restore.ExitCode == 0, restore.Tail());
        string assets = File.ReadAllText(Repo.TestProjectPath("NetPrivateStatic", "obj", "project.assets.json"));
        Assert.Contains("PrivateStatic.asmproj", assets);
        Assert.DoesNotContain("Sectionless.asmproj", assets);
    }

    /// <summary>
    /// PrivateAssets="none" on the reference keeps the static library a dependency of the shared
    /// library's package: the opt-out of the default, for a package that ships the static library
    /// too (tests/projects/PrivateStatic with -p:PrivateStaticAssets=none).
    /// </summary>
    [Fact]
    public void PrivateAssets_none_keeps_a_static_library_a_package_dependency()
    {
        string output = Path.Combine(Repo.Artifacts, "test-packages", "private-static-none");
        CommandResult pack = sdk.Dotnet("pack", Repo.TestProject("PrivateStatic"), "-c", "PrivateStatic", "-p:PrivateStaticAssets=none", "-o", output);
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        Assert.Contains("<dependency id=\"sectionless\"", Nuspec(Path.Combine(output, "Test.PrivateStatic.1.0.0.nupkg"), "Test.PrivateStatic.nuspec"));
    }

    /// <summary>Reads the .nuspec inside a package.</summary>
    /// <param name="nupkg">The .nupkg file.</param>
    /// <param name="name">The .nuspec's entry name (the package id, then .nuspec).</param>
    /// <returns>The .nuspec's text.</returns>
    /// <exception cref="FileNotFoundException">The package does not exist.</exception>
    /// <exception cref="Xunit.Sdk.XunitException">The package has no such entry.</exception>
    private static string Nuspec(string nupkg, string name)
    {
        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        ZipArchiveEntry? entry = archive.GetEntry(name);
        Assert.True(entry is not null, $"{nupkg} has no {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>A project consumes a package by version: samples/consumers/Hello.Consumer builds against Hello.Static 1.0.0.</summary>
    [Fact]
    public void A_project_consumes_a_package_by_version()
    {
        CommandResult build = BuildConsumer(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
    }
}
