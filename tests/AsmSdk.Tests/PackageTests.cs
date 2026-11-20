using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Packages built from .asmproj projects: their layouts (one per target framework), and
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

    /// <summary>A project consumes a package by version: samples/consumers/Hello.Consumer builds against Hello.Static 1.0.0.</summary>
    [Fact]
    public void A_project_consumes_a_package_by_version()
    {
        CommandResult build = BuildConsumer(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
    }
}
