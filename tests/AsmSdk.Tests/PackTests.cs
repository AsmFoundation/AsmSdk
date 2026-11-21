using System.Text.Json;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The packages under test: the SDK and every staged toolchain pack, and every consumer in the
/// repository pins the version being tested.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class PackTests(SdkUnderTest sdk)
{
    /// <summary>
    /// The samples, the test projects and the scenarios pin the version Directory.Build.props
    /// sets: otherwise they would restore another AsmFoundation.Sdk from nuget.org and test
    /// nothing of this one.
    /// </summary>
    /// <param name="globalJson">The global.json, relative to the repository root.</param>
    [Theory]
    [InlineData("samples/global.json")]
    [InlineData("tests/projects/global.json")]
    [InlineData("tests/scenarios/global.json")]
    public void Consumers_pin_the_version_under_test(string globalJson)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, globalJson)));
        string pinned = json.RootElement.GetProperty("msbuild-sdks").GetProperty("AsmFoundation.Sdk").GetString()!;
        Assert.Equal(sdk.Version, pinned);
    }

    /// <summary>AsmFoundation.Sdk packs.</summary>
    [Fact]
    public void The_sdk_packs() =>
        Assert.True(sdk.PackSdk.ExitCode == 0, sdk.PackSdk.Tail());

    /// <summary>A toolchain package packs for every staged RID, this machine's among them.</summary>
    [Fact]
    public void Every_staged_toolchain_packs()
    {
        Assert.Contains(Repo.HostRid, sdk.StagedRids);
        foreach ((string rid, CommandResult pack) in sdk.PackToolchains)
        {
            Assert.True(pack.ExitCode == 0, $"AsmFoundation.Toolchain.{rid}: {pack.Tail()}");
        }
    }

    /// <summary>
    /// samples/Samples.slnx builds with the bundled toolchain and TreatWarningsAsErrors, without
    /// a single warning.
    /// </summary>
    [Fact]
    public void The_samples_build_without_warnings() =>
        Assert.True(sdk.SamplesBuild.ExitCode == 0 && sdk.SamplesBuild.StdOut.Contains(" 0 Warning(s)", StringComparison.Ordinal),
            sdk.SamplesBuild.Tail());
}
