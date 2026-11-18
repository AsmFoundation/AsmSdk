using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>
/// Paths and facts of the AsmSdk repository under test: where the samples, the test projects
/// and the artifacts are, which machine this is, and which SDK version is being tested.
/// </summary>
internal static partial class Repo
{
    /// <summary>
    /// The repository root, the folder that holds AsmSdk.slnx, found by walking up from the
    /// test assembly's folder (bin/&lt;Configuration&gt;/net10.0 under tests/AsmSdk.Tests).
    /// </summary>
    public static string Root { get; } = FindRoot();

    /// <summary>samples/: the projects that consume the SDK as a user's repository would.</summary>
    public static string Samples => Path.Combine(Root, "samples");

    /// <summary>tests/projects/: the projects that each exercise one SDK rule (some must fail).</summary>
    public static string TestProjects => Path.Combine(Root, "tests", "projects");

    /// <summary>artifacts/: packages, the staged toolchain, the isolated NuGet cache.</summary>
    public static string Artifacts => Path.Combine(Root, "artifacts");

    /// <summary>
    /// This machine's portable RID (win-x64, linux-x64, osx-arm64, ...), the suffix of the
    /// toolchain package that restores on it.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">An OS or CPU no toolchain package exists for (thrown by the initializer).</exception>
    public static string HostRid { get; } = ComputeHostRid();

    /// <summary>A path under samples/.</summary>
    /// <param name="parts">The path's parts below samples/.</param>
    /// <returns>The full path.</returns>
    public static string Sample(params string[] parts) => Path.Combine([Samples, .. parts]);

    /// <summary>The project file of a test project: tests/projects/&lt;name&gt;/&lt;name&gt;.asmproj.</summary>
    /// <param name="name">The folder name, which is also the project name.</param>
    /// <returns>The full path of the project file.</returns>
    public static string TestProject(string name) => Path.Combine(TestProjects, name, name + ".asmproj");

    /// <summary>A path under tests/projects/.</summary>
    /// <param name="parts">The path's parts below tests/projects/.</param>
    /// <returns>The full path.</returns>
    public static string TestProjectPath(params string[] parts) => Path.Combine([TestProjects, .. parts]);

    /// <summary>The AsmFoundation.Sdk version under test: Directory.Build.props's &lt;Version&gt;.</summary>
    /// <returns>The version, for example "0.2.0".</returns>
    /// <exception cref="InvalidOperationException">Directory.Build.props sets no version (nothing could be tested).</exception>
    public static string SdkVersion()
    {
        string props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        Match match = VersionElement().Match(props);
        return match.Success
            ? match.Groups[1].Value.Trim()
            : throw new InvalidOperationException("Directory.Build.props sets no <Version>");
    }

    /// <summary>A path relative to the repository root, with forward slashes (for WSL and messages).</summary>
    /// <param name="path">A path inside the repository.</param>
    /// <returns>The relative path.</returns>
    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    /// <summary>Walks up from the test assembly's folder to the folder that holds AsmSdk.slnx.</summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="InvalidOperationException">The test assembly is not inside an AsmSdk checkout.</exception>
    private static string FindRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "AsmSdk.slnx")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException($"No AsmSdk.slnx above {AppContext.BaseDirectory}: run the tests from an AsmSdk checkout");
    }

    /// <summary>Maps the running OS and CPU to the toolchain packages' RID naming.</summary>
    /// <returns>The RID.</returns>
    /// <exception cref="PlatformNotSupportedException">An OS or CPU no toolchain package exists for.</exception>
    private static string ComputeHostRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : throw new PlatformNotSupportedException($"No toolchain package for {RuntimeInformation.OSDescription}");
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture other => throw new PlatformNotSupportedException($"No toolchain package for {other}"),
        };
        return $"{os}-{arch}";
    }

    /// <summary>&lt;Version&gt;…&lt;/Version&gt; in an MSBuild file.</summary>
    /// <returns>The regex; group 1 is the version.</returns>
    [GeneratedRegex("<Version>([^<]+)</Version>")]
    private static partial Regex VersionElement();
}
