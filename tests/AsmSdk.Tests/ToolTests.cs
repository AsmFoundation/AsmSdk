using System.IO.Compression;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// PackAsTool: an asm program packed as a .NET tool (tests/projects/EverywhereTool), in the
/// package kinds the .NET 10 SDK writes for a platform-specific tool, installed with dotnet tool
/// install and run: here, and in Linux containers (glibc and musl) where Docker runs them.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class ToolTests(SdkUnderTest sdk)
{
    /// <summary>The tool project.</summary>
    private static readonly string Tool = Repo.TestProject("EverywhereTool");

    /// <summary>Packs EverywhereTool into the local feed once: the top-level package and one per RID.</summary>
    /// <returns>The pack's result, asserted successful.</returns>
    private CommandResult PackTool()
    {
        CommandResult pack = sdk.Once("pack EverywhereTool", () => sdk.Dotnet("pack", Tool, "-c", "Release"));
        Assert.True(pack.ExitCode == 0, pack.Tail());
        return pack;
    }

    /// <summary>A package in the local feed.</summary>
    /// <param name="id">The package id.</param>
    /// <returns>The .nupkg path.</returns>
    private static string Package(string id) => Path.Combine(Repo.Artifacts, "packages", $"{id}.1.0.0.nupkg");

    /// <summary>One entry of a package, as text.</summary>
    /// <param name="nupkg">The package.</param>
    /// <param name="entry">The entry's path.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="InvalidOperationException">The package has no such entry (the test then fails with its name).</exception>
    /// <exception cref="IOException">The package cannot be read.</exception>
    private static string Entry(string nupkg, string entry)
    {
        using ZipArchive zip = ZipFile.OpenRead(nupkg);
        using var reader = new StreamReader(zip.GetEntry(entry)?.Open() ?? throw new InvalidOperationException($"{nupkg} has no {entry}"));
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The top-level package is a pointer (PackageType DotnetTool): only
    /// tools/any/any/DotnetToolSettings.xml, with the command and one RuntimeIdentifierPackage per
    /// RID, and no dependency.
    /// </summary>
    [Fact]
    public void PackAsTool_writes_a_pointer_package()
    {
        PackTool();
        string nupkg = Package("Test.EverywhereTool");
        Assert.Equal(["Test.EverywhereTool.nuspec", "tools/any/any/DotnetToolSettings.xml"], SdkUnderTest.PackageEntries(nupkg));
        string nuspec = Entry(nupkg, "Test.EverywhereTool.nuspec");
        Assert.Contains("<packageType name=\"DotnetTool\" />", nuspec, StringComparison.Ordinal);
        Assert.DoesNotContain("<dependency ", nuspec, StringComparison.Ordinal);
        string settings = Entry(nupkg, "tools/any/any/DotnetToolSettings.xml");
        Assert.Contains("<Command Name=\"everywhere-tool\" />", settings, StringComparison.Ordinal);
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.Contains($"<RuntimeIdentifierPackage RuntimeIdentifier=\"{rid}\" Id=\"Test.EverywhereTool.{rid}\" />", settings, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Each RID's package (PackageType DotnetToolRidPackage) holds tools/any/&lt;rid&gt;/: the program,
    /// the library next to it, and DotnetToolSettings.xml that starts the program directly
    /// (Runner="executable": no .NET runtime involved).
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void PackAsTool_writes_a_package_per_rid(string rid)
    {
        PackTool();
        string nupkg = Package($"Test.EverywhereTool.{rid}");
        string program = ProgramTests.ProgramFile(rid, "everywhere-tool");
        Assert.Equal(
            new[] { $"Test.EverywhereTool.{rid}.nuspec", $"tools/any/{rid}/DotnetToolSettings.xml", $"tools/any/{rid}/{program}", $"tools/any/{rid}/{CrossPlatformTests.LibraryFile(rid, "everywhere")}" }.Order(StringComparer.Ordinal),
            SdkUnderTest.PackageEntries(nupkg));
        Assert.Contains("<packageType name=\"DotnetToolRidPackage\" />", Entry(nupkg, $"Test.EverywhereTool.{rid}.nuspec"), StringComparison.Ordinal);
        Assert.Contains($"<Command Name=\"everywhere-tool\" EntryPoint=\"{program}\" Runner=\"executable\" />", Entry(nupkg, $"tools/any/{rid}/DotnetToolSettings.xml"), StringComparison.Ordinal);
    }

    /// <summary>
    /// dotnet tool install (into a tool path) installs this machine's package, and the command
    /// starts the program, which finds its library: the exit status names this machine's RID and
    /// counts the arguments.
    /// </summary>
    [Fact]
    public void The_tool_installs_and_runs_here()
    {
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no program for this machine ({Repo.HostRid})");
        }

        PackTool();
        string toolPath = Path.Combine(Repo.Artifacts, "test-run", "tool-install");
        if (Directory.Exists(toolPath))
        {
            Directory.Delete(toolPath, recursive: true);
        }

        // tests/projects/nuget.config: the local feed for Test.*, through its source mapping
        // (with a mapping in force, dotnet tool install refuses --add-source).
        CommandResult install = sdk.DotnetCommand("tool", "install", "--tool-path", toolPath, "Test.EverywhereTool", "--version", "1.0.0",
            "--configfile", Path.Combine(Repo.TestProjects, "nuget.config"));
        Assert.True(install.ExitCode == 0, install.Tail());
        CommandResult run = OperatingSystem.IsWindows()
            ? Command.Run("cmd", ["/c", Path.Combine(toolPath, "everywhere-tool.cmd"), "installed"], Repo.Root)
            : Command.Run(Path.Combine(toolPath, "everywhere-tool"), ["installed"], Repo.Root);
        int expected = 10 * CrossPlatformTests.PlatformCode[rid] + 1;
        Assert.True(run.ExitCode == expected, $"exit {run.ExitCode}, want {expected}: {run.Output}");
        Assert.Contains("installed", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The .NET SDK images and the RID whose package each installs.</summary>
    public static readonly TheoryData<string, string> SdkImages = new()
    {
        { "mcr.microsoft.com/dotnet/sdk:10.0", "linux-x64" },
        { "mcr.microsoft.com/dotnet/sdk:10.0-alpine", "linux-musl-x64" },
    };

    /// <summary>
    /// In a Linux container with the .NET SDK, dotnet tool install -g picks the package of that
    /// machine's C library (glibc, or musl on Alpine), and the command, a symbolic link into the
    /// tool store, starts the program, whose run path ($ORIGIN) still finds the library.
    /// </summary>
    /// <param name="image">The SDK image.</param>
    /// <param name="rid">The RID its package must be.</param>
    [Theory]
    [MemberData(nameof(SdkImages))]
    public void The_tool_installs_and_runs_in_a_linux_container(string image, string rid)
    {
        if (!Command.OnPath("docker") || Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root).StdOut.Trim() != "linux"
            || Command.Run("docker", ["run", "--rm", "--platform", "linux/amd64", "alpine:3", "true"], Repo.Root).ExitCode != 0)
        {
            Assert.Skip("No Docker running linux/amd64 containers here");
        }

        PackTool();
        string feed = Path.Combine(Repo.Artifacts, "packages");
        CommandResult run = Command.Run("docker", ["run", "--rm", "--platform", "linux/amd64", "-v", $"{feed}:/feed:ro", image, "sh", "-c",
            "export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_PACKAGES=/tmp/nuget"
            + " && dotnet tool install -g Test.EverywhereTool --version 1.0.0 --add-source /feed >/dev/null"
            + " && readlink ~/.dotnet/tools/everywhere-tool && ~/.dotnet/tools/everywhere-tool in-container"], Repo.Root);
        int expected = 10 * CrossPlatformTests.PlatformCode[rid] + 1;
        Assert.True(run.ExitCode == expected, $"exit {run.ExitCode}, want {expected}: {run.Output}");
        Assert.Contains($"test.everywheretool.{rid}/1.0.0/tools/any/{rid}/everywhere-tool", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("in-container", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>PackAsTool on something that is not a program stops with ASM0053.</summary>
    [Fact]
    public void PackAsTool_on_a_library_is_an_error()
    {
        CommandResult pack = sdk.Once("pack Everywhere as a tool", () => sdk.Dotnet("pack", Repo.TestProject("Everywhere"), "-c", "Release", "-p:PackAsTool=true", "-o", Path.Combine(Repo.Artifacts, "test-run", "not-a-tool")));
        Assert.True(pack.ExitCode != 0, $"a library packed as a tool: {pack.Tail()}");
        Assert.Contains("ASM0053", pack.StdOut, StringComparison.Ordinal);
    }
}
