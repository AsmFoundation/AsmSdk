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

    /// <summary>
    /// The environment every dotnet command of the tests gets: the isolated NuGet cache, no
    /// telemetry or logo, none of the host's MSBuild variables (set by <see cref="InitializeAsync"/>).
    /// For runners that start their own commands with it, such as the scenarios', which add
    /// their own variables to a copy.
    /// </summary>
    public IReadOnlyDictionary<string, string?> TestEnvironment => _environment;

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
    /// dotnet pack of AsmFoundation.Templates (the dotnet new templates), at the SDK's version,
    /// into the local feed: the plug-and-play scenarios install it from there with
    /// dotnet new install, and the projects it creates name this SDK version.
    /// </summary>
    public CommandResult PackTemplates { get; private set; } = null!;

    /// <summary>
    /// The Release build of samples/Samples.slnx, at normal verbosity: several tests read the
    /// NASM and linker command lines from its log.
    /// </summary>
    public CommandResult SamplesBuild { get; private set; } = null!;

    /// <summary>
    /// Packs the SDK, the staged toolchains and the templates, after removing everything a
    /// previous run left (the NuGet cache, test packages, every sample's and test project's bin/
    /// and obj/), then builds the samples.
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

        // Git Bash starts Windows programs without ProgramFiles(x86) (it drops variable names
        // with parentheses), and NativeAOT finds Visual Studio's linker by running vswhere.exe
        // from the Visual Studio Installer folder: a NativeAOT publish from a suite started in
        // Git Bash failed with "'vswhere.exe' is not recognized" (measured 2026-11-20, also
        // with ProgramFiles(x86) restored alone). The variable is restored from the OS, as cmd
        // and PowerShell would pass it, and the Installer folder goes on PATH, which is what
        // made the publish work.
        if (OperatingSystem.IsWindows())
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (programFilesX86.Length != 0)
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ProgramFiles(x86)")))
                {
                    _environment["ProgramFiles(x86)"] = programFilesX86;
                }

                string installer = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer");
                if (File.Exists(Path.Combine(installer, "vswhere.exe")))
                {
                    _environment["PATH"] = installer + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                }
            }
        }

        Clean();
        Version = Repo.SdkVersion();
        PackSdk = Dotnet("pack", "src/AsmFoundation.Sdk/AsmFoundation.Sdk.csproj", "-c", "Release");
        PackToolchains = StagedRids.ToDictionary(rid => rid, rid =>
            Dotnet("pack", "toolchain/AsmFoundation.Toolchain.csproj", "-c", "Release", $"-p:ToolchainRid={rid}"));
        PackTemplates = Dotnet("pack", "src/AsmFoundation.Templates/AsmFoundation.Templates.csproj", "-c", "Release");
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

    /// <summary>
    /// Runs a <c>dotnet</c> command that is not an MSBuild build (<c>dotnet tool install</c>,
    /// <c>dotnet nuget</c>) with the test environment, its arguments exactly as given.
    /// </summary>
    /// <param name="arguments">The arguments after <c>dotnet</c>.</param>
    /// <returns>The command's result.</returns>
    /// <remarks>
    /// Not through <see cref="Dotnet"/>: its -nologo and -nodeReuse:false are MSBuild switches,
    /// and dotnet tool install rejects them with its usage text (measured). The test
    /// environment still applies: the isolated NuGet cache, no MSBuild variables of the host.
    /// </remarks>
    public CommandResult DotnetCommand(params string[] arguments) =>
        Command.Run("dotnet", arguments, Repo.Root, _environment);

    /// <summary>
    /// Runs <c>dotnet run</c> of a project with the test environment: its options, then the
    /// program's own arguments after "--".
    /// </summary>
    /// <param name="project">The project file.</param>
    /// <param name="options">dotnet run options (for example -c Release, -r linux-x64, -p:AsmRunIn=Docker).</param>
    /// <param name="programArguments">The arguments the program must receive, each as one argument.</param>
    /// <returns>The command's result: the program's exit status and output when it ran, dotnet's otherwise.</returns>
    /// <remarks>
    /// Not through <see cref="Dotnet"/>: its -nologo and -nodeReuse:false go last, and after
    /// "--" they would be the program's arguments. Node reuse is turned off with
    /// MSBUILDDISABLENODEREUSE instead, for the reason <see cref="Dotnet"/> gives.
    /// </remarks>
    public CommandResult DotnetRun(string project, string[] options, params string[] programArguments)
    {
        var environment = new Dictionary<string, string?>(_environment, StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };
        return Command.Run("dotnet", ["run", "--project", project, .. options, "--", .. programArguments], Repo.Root, environment);
    }

    /// <summary>
    /// Runs <c>dotnet run</c> of a project as the other overload does, with variables of the
    /// caller's added to the test environment: what a user's shell has set when they type
    /// dotnet run, which the CLI and whatever it starts inherit. Unlike <c>-e</c>, no build
    /// ever hears of them, so they show what reaches a program without the CLI's help.
    /// </summary>
    /// <param name="project">The project file.</param>
    /// <param name="options">dotnet run options (for example -c Release).</param>
    /// <param name="variables">The variables to set; a null value removes one from the test environment.</param>
    /// <param name="programArguments">The arguments the program must receive, each as one argument.</param>
    /// <returns>The command's result: the program's exit status and output when it ran, dotnet's otherwise.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">dotnet could not be started (none on PATH).</exception>
    /// <remarks>
    /// MSBUILDDISABLENODEREUSE is set after the caller's variables, so a caller cannot turn
    /// node reuse back on: a reused node would keep an old task assembly loaded after a repack.
    /// </remarks>
    public CommandResult DotnetRun(string project, string[] options, IReadOnlyDictionary<string, string?> variables, params string[] programArguments)
    {
        var environment = new Dictionary<string, string?>(_environment, StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> variable in variables)
        {
            environment[variable.Key] = variable.Value;
        }

        environment["MSBUILDDISABLENODEREUSE"] = "1";
        return Command.Run("dotnet", ["run", "--project", project, .. options, "--", .. programArguments], Repo.Root, environment);
    }

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

    /// <summary>
    /// The major version of the .NET SDK the tests run (dotnet --version at the repository root,
    /// once): what the CLI does differs between the SDK lines CI runs. The hosted Windows images
    /// bring the .NET 11 SDK, which outranks setup-dotnet's 10.0.x where both share a folder; the
    /// other runners have 10.0.x alone. The .NET 11 SDK's dotnet run passes -e to the build, for
    /// one; the .NET 10 SDK's passes it to none.
    /// </summary>
    /// <returns>The major version, such as 10 or 11.</returns>
    /// <exception cref="InvalidOperationException">dotnet --version failed or printed no version.</exception>
    public int SdkMajorVersion()
    {
        CommandResult version = Once("dotnet --version", () => DotnetCommand("--version"));
        string text = version.StdOut.Trim();
        int dot = text.IndexOf('.');
        if (version.ExitCode != 0 || dot <= 0 || !int.TryParse(text[..dot], out int major))
        {
            throw new InvalidOperationException($"dotnet --version did not say which SDK runs: {version.Tail()}");
        }

        return major;
    }

    /// <summary>GNU readelf-style output of the bundled llvm-readobj.</summary>
    /// <param name="path">The ELF file or archive to inspect.</param>
    /// <param name="options">readelf options, such as -h, -d, -l, -S, --dyn-syms, -s.</param>
    /// <returns>The tool's standard output (empty when it failed, which the caller's check then reports).</returns>
    public string ReadElf(string path, params string[] options)
    {
        string exe = Path.Combine(Tools, OperatingSystem.IsWindows() ? "llvm-readobj.exe" : "llvm-readobj");
        return Command.Run(exe, ["--elf-output-style=GNU", .. options, path]).StdOut;
    }

    /// <summary>
    /// llvm-readobj's own output style, for any object format: ELF, PE/COFF and Mach-O alike
    /// (the GNU style of <see cref="ReadElf"/> exists for ELF only).
    /// </summary>
    /// <param name="path">The file to inspect.</param>
    /// <param name="options">llvm-readobj options, such as --file-headers, --coff-exports, --dyn-syms, --symbols, --needed-libs, --coff-imports.</param>
    /// <returns>The tool's standard output (empty when it failed, which the caller's check then reports).</returns>
    public string ReadObj(string path, params string[] options)
    {
        string exe = Path.Combine(Tools, OperatingSystem.IsWindows() ? "llvm-readobj.exe" : "llvm-readobj");
        return Command.Run(exe, [.. options, path]).StdOut;
    }

    /// <summary>
    /// Builds a test project of tests/projects in Release with extra arguments, once per
    /// distinct argument list.
    /// </summary>
    /// <param name="name">The test project's name.</param>
    /// <param name="arguments">More dotnet build arguments (for example -r linux-arm64).</param>
    /// <returns>The build's result.</returns>
    public CommandResult BuildTestProject(string name, params string[] arguments) =>
        arguments.Length == 0
            ? BuildTestProject(name)
            : Once($"build {name} {string.Join(' ', arguments)}", () => Dotnet(["build", Repo.TestProject(name), "-c", "Release", .. arguments]));

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
    /// the input files of the tool runs, the scenarios' work folders, the sample, test and
    /// scenario packages in the local feed, and every project's bin/ and obj/.
    /// </summary>
    private static void Clean()
    {
        DeleteDirectory(Path.Combine(Repo.Artifacts, "test-nuget"));
        DeleteDirectory(Path.Combine(Repo.Artifacts, "test-packages"));
        DeleteDirectory(Path.Combine(Repo.Artifacts, "test-run"));
        DeleteDirectory(ScenarioRunner.WorkRoot);
        string feed = Path.Combine(Repo.Artifacts, "packages");
        if (Directory.Exists(feed))
        {
            // Only the packages built from samples/, tests/projects and tests/scenarios: the SDK
            // and toolchain packages are repacked by InitializeAsync, under the same names.
            foreach (string stale in new[] { "Hello.*.nupkg", "Samples.*.nupkg", "Test.*.nupkg", "Scenario.*.nupkg" }
                         .SelectMany(pattern => Directory.GetFiles(feed, pattern)))
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
