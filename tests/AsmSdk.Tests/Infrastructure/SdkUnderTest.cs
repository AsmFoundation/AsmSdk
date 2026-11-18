using System.Collections.Concurrent;
using System.IO.Compression;
using Xunit;

[assembly: AssemblyFixture(typeof(AsmSdk.Tests.Infrastructure.SdkUnderTest))]

namespace AsmSdk.Tests.Infrastructure;

/// <summary>
/// The SDK under test, set up once for the whole test assembly: the packages are packed from
/// this checkout, and samples/Samples.slnx is built with them, against a NuGet cache of its own.
/// </summary>
/// <remarks>
/// <para>
/// Why a fresh NuGet cache (artifacts/test-nuget): the SDK's version does not change between
/// iterations, and a shared cache keeps the first copy of a version forever, so the tests
/// would silently run against a stale SDK.
/// </para>
/// <para>
/// Steps that several tests need (a package, a test project's build) run once, on first use,
/// through <see cref="Once"/>. The tests run one at a time (xunit.runner.json), because they
/// build the same projects and some of them change files (the incremental build tests).
/// </para>
/// </remarks>
public sealed class SdkUnderTest : IAsyncLifetime
{
    /// <summary>Results of the steps that ran, by name, so each runs once.</summary>
    private readonly ConcurrentDictionary<string, Lazy<CommandResult>> _steps = new();

    /// <summary>The environment every dotnet command gets (see <see cref="InitializeAsync"/>).</summary>
    private readonly Dictionary<string, string?> _environment = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The staged toolchain folder of this machine: artifacts/toolchain/&lt;rid&gt;/tools.</summary>
    public string Tools { get; private set; } = "";

    /// <summary>The RIDs whose toolchain is staged (each gets a toolchain package).</summary>
    public IReadOnlyList<string> StagedRids { get; private set; } = [];

    /// <summary>The AsmFoundation.Sdk version under test.</summary>
    public string Version { get; private set; } = "";

    /// <summary>dotnet pack of AsmFoundation.Sdk.</summary>
    public CommandResult PackSdk { get; private set; } = null!;

    /// <summary>dotnet pack of the toolchain package, per staged RID.</summary>
    public IReadOnlyDictionary<string, CommandResult> PackToolchains { get; private set; } = null!;

    /// <summary>
    /// The Release build of samples/Samples.slnx, at normal verbosity: several tests read the
    /// NASM and linker command lines from its log.
    /// </summary>
    public CommandResult SamplesBuild { get; private set; } = null!;

    /// <summary>
    /// Packs the SDK and the staged toolchains, after removing everything a previous run left
    /// (the NuGet cache, test packages, every sample's and test project's bin/ and obj/), then
    /// builds the samples.
    /// </summary>
    /// <returns>A completed task (the work is synchronous).</returns>
    /// <exception cref="InvalidOperationException">
    /// This machine's toolchain is not staged: nothing could be built, and every test fails
    /// with the command that stages it.
    /// </exception>
    public ValueTask InitializeAsync()
    {
        string staging = Path.Combine(Repo.Artifacts, "toolchain");
        StagedRids = Directory.Exists(staging)
            ? Directory.GetDirectories(staging)
                .Where(dir => File.Exists(Path.Combine(dir, "tools", "VERSIONS.txt")))
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
        if (!StagedRids.Contains(Repo.HostRid))
        {
            throw new InvalidOperationException(
                $"No staged toolchain for {Repo.HostRid}. Run: dotnet run toolchain/fetch.cs -- --rid {Repo.HostRid}");
        }

        Tools = Path.Combine(staging, Repo.HostRid, "tools");
        if (!OperatingSystem.IsWindows())
        {
            // CI artifacts (and NuGet) drop the execute bit; llvm-readobj is called directly.
            foreach (string tool in Directory.GetFiles(Tools).Where(f => !f.EndsWith(".txt", StringComparison.Ordinal)))
            {
                File.SetUnixFileMode(tool, File.GetUnixFileMode(tool) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }

        // The test host may run inside an IDE or under `dotnet test`, which can leave
        // MSBuild's own variables (MSBuildExtensionsPath, MSBUILD_EXE_PATH, ...) in the
        // environment; a nested dotnet must resolve its SDK from scratch, as a user's would.
        foreach (string name in Environment.GetEnvironmentVariables().Keys.OfType<string>()
                     .Where(n => n.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)))
        {
            _environment[name] = null;
        }

        _environment["NUGET_PACKAGES"] = Path.Combine(Repo.Artifacts, "test-nuget");
        _environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        _environment["DOTNET_NOLOGO"] = "1";

        Clean();
        Version = Repo.SdkVersion();
        PackSdk = Dotnet("pack", "src/AsmFoundation.Sdk/AsmFoundation.Sdk.csproj", "-c", "Release");
        PackToolchains = StagedRids.ToDictionary(rid => rid, rid =>
            Dotnet("pack", "toolchain/AsmFoundation.Toolchain.csproj", "-c", "Release", $"-p:ToolchainRid={rid}"));
        SamplesBuild = Dotnet("build", "samples/Samples.slnx", "-c", "Release", "-v:n");
        return ValueTask.CompletedTask;
    }

    /// <summary>Nothing to release: the build outputs stay for inspection.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Runs <c>dotnet</c> with the test environment and without MSBuild node reuse.</summary>
    /// <param name="arguments">The arguments after <c>dotnet</c>; paths relative to the repository root.</param>
    /// <returns>The command's result.</returns>
    /// <remarks>
    /// Node reuse is off because a long-lived MSBuild node caches SDK files: an SDK repacked
    /// under the same version would otherwise go unnoticed between test runs.
    /// </remarks>
    public CommandResult Dotnet(params string[] arguments) =>
        Command.Run("dotnet", [.. arguments, "-nologo", "-nodeReuse:false"], Repo.Root, _environment);

    /// <summary>Runs a step once and remembers its result for every later caller.</summary>
    /// <param name="name">The step's name (its cache key).</param>
    /// <param name="step">The step.</param>
    /// <returns>The step's result, from the first call.</returns>
    public CommandResult Once(string name, Func<CommandResult> step) =>
        _steps.GetOrAdd(name, _ => new Lazy<CommandResult>(step)).Value;

    /// <summary>Builds a test project of tests/projects in Release, once.</summary>
    /// <param name="name">The test project's name.</param>
    /// <returns>The build's result.</returns>
    public CommandResult BuildTestProject(string name) =>
        Once($"build {name}", () => Dotnet("build", Repo.TestProject(name), "-c", "Release"));

    /// <summary>GNU readelf-style output of the bundled llvm-readobj.</summary>
    /// <param name="path">The ELF file or archive to inspect.</param>
    /// <param name="options">readelf options, such as -h, -d, -l, -S, --dyn-syms, -s.</param>
    /// <returns>The tool's standard output (empty when it failed, which the caller's check then reports).</returns>
    public string ReadElf(string path, params string[] options)
    {
        string exe = Path.Combine(Tools, OperatingSystem.IsWindows() ? "llvm-readobj.exe" : "llvm-readobj");
        return Command.Run(exe, ["--elf-output-style=GNU", .. options, path]).StdOut;
    }

    /// <summary>The files of a package, without NuGet's own bookkeeping entries.</summary>
    /// <param name="nupkg">The .nupkg file.</param>
    /// <returns>The entry names, with forward slashes; empty when the package does not exist.</returns>
    public static SortedSet<string> PackageEntries(string nupkg)
    {
        var entries = new SortedSet<string>(StringComparer.Ordinal);
        if (!File.Exists(nupkg))
        {
            return entries;
        }

        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("_rels/", StringComparison.Ordinal)
                && !entry.FullName.StartsWith("package/", StringComparison.Ordinal)
                && entry.FullName != "[Content_Types].xml")
            {
                entries.Add(entry.FullName);
            }
        }

        return entries;
    }

    /// <summary>
    /// Removes what a previous run left: the isolated NuGet cache, the test package folder,
    /// the sample and test packages in the local feed, and every project's bin/ and obj/.
    /// </summary>
    private static void Clean()
    {
        DeleteDirectory(Path.Combine(Repo.Artifacts, "test-nuget"));
        DeleteDirectory(Path.Combine(Repo.Artifacts, "test-packages"));
        string feed = Path.Combine(Repo.Artifacts, "packages");
        if (Directory.Exists(feed))
        {
            foreach (string stale in Directory.GetFiles(feed, "Hello.*.nupkg").Concat(Directory.GetFiles(feed, "Test.*.nupkg")))
            {
                File.Delete(stale);
            }
        }

        foreach (string root in new[] { Repo.Samples, Repo.TestProjects })
        {
            foreach (string project in Directory.EnumerateFiles(root, "*proj", SearchOption.AllDirectories))
            {
                string folder = Path.GetDirectoryName(project)!;
                DeleteDirectory(Path.Combine(folder, "bin"));
                DeleteDirectory(Path.Combine(folder, "obj"));
            }
        }
    }

    /// <summary>Deletes a folder and everything in it, if it exists.</summary>
    /// <param name="path">The folder.</param>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
