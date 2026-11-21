using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>
/// Runs a <see cref="Scenario"/>: copies its folder into artifacts/scenarios (with the shared
/// files of tests/scenarios), starts its services, runs each step with the SDK under test's
/// environment, and checks what each step must produce, writing every command, its exit status,
/// its duration and its output to the test's log.
/// </summary>
/// <remarks>
/// <para>
/// Why a copy: each run starts from the files as committed (no bin/ or obj/ of an earlier run,
/// no publish folder, no installed tool), whatever a developer built in tests/scenarios, and
/// what the steps write stays under artifacts/. artifacts/scenarios sits as deep under the
/// repository as tests/scenarios, so the shared nuget.config's relative feed path
/// (../../artifacts/packages) means the same folder in both places: a scenario also builds where
/// it is, opened in an IDE.
/// </para>
/// <para>
/// The steps run in that copy with the environment every dotnet command of the tests gets (the
/// isolated NuGet cache in artifacts/test-nuget, no MSBuild variables of the host), plus
/// MSBUILDDISABLENODEREUSE=1: node reuse is turned off through the environment instead of
/// -nodeReuse:false because a step's arguments are its author's, and some verbs (dotnet tool,
/// and dotnet run after "--") must not get MSBuild switches.
/// </para>
/// </remarks>
internal static class ScenarioRunner
{
    /// <summary>artifacts/scenarios/: where scenarios run, each in a folder of its name.</summary>
    public static string WorkRoot => Path.Combine(Repo.Artifacts, "scenarios");

    /// <summary>How long a service may take to show its ready text or accept a connection, image pull excluded.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Folders never copied into the work folder: build outputs and IDE state of an in-place build.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", ".idea" };

    /// <summary>
    /// Why Docker cannot run a platform's containers here (null when it can), probed once per
    /// platform and test run: each probe starts a container, which takes seconds.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<string?>> DockerProbes = new(StringComparer.Ordinal);

    /// <summary>
    /// Why a scenario cannot run on this machine, or null when it can: a <c>rids</c> list that
    /// leaves this machine out, a machine AsmSdk builds nothing for, or a service whose platform
    /// no Docker here runs.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The reason to skip, for the test report; null to run.</returns>
    public static string? SkipReason(Scenario scenario)
    {
        if (scenario.Rids is { } rids && !rids.Contains(Repo.HostRid, StringComparer.Ordinal))
        {
            return $"{scenario.Name} runs on {string.Join(", ", rids)}; this machine is {Repo.HostRid}";
        }

        if (CrossPlatformTests.HostBuildRid() is null)
        {
            return $"AsmSdk builds nothing for this machine ({Repo.HostRid})";
        }

        foreach (ScenarioService service in scenario.Services)
        {
            if (DockerUnavailable(service.Platform) is { } reason)
            {
                return $"{scenario.Name} needs Docker for its {service.Name} service ({service.Image}, {service.Platform}): {reason}";
            }
        }

        return null;
    }

    /// <summary>
    /// Runs every step of a scenario in a fresh copy of its folder, with its services, and
    /// fails the calling test at the first step that does not produce what it must.
    /// </summary>
    /// <param name="scenario">The scenario (loaded and validated).</param>
    /// <param name="sdk">The SDK under test, whose environment the steps get.</param>
    /// <param name="log">The test's log: every command, exit status, duration and output.</param>
    /// <exception cref="Exception">
    /// Xunit's assertion exception for the first unmet expectation (it names the scenario, the
    /// step, the command, what was expected and the output's tail); an
    /// <see cref="InvalidOperationException"/> or <see cref="TimeoutException"/> when a service
    /// cannot start or never becomes ready; an <see cref="IOException"/> when the work folder
    /// cannot be prepared.
    /// </exception>
    public static void Run(Scenario scenario, SdkUnderTest sdk, ITestOutputHelper log)
    {
        string folder = Prepare(scenario);
        log.WriteLine($"{scenario.Name}: {scenario.Description}");
        log.WriteLine($"(in {Repo.Relative(folder)}, a fresh copy of {Repo.Relative(scenario.SourceFolder)}; this machine is {Repo.HostRid})");

        // Services stop whatever happens to the steps: a failed assertion unwinds through here.
        using ScenarioServices services = ScenarioServices.Start(scenario, log);
        var environment = new Dictionary<string, string?>(sdk.TestEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        for (int i = 0; i < scenario.Steps.Count; i++)
        {
            ScenarioStep step = scenario.Steps[i];
            string label = $"step {i + 1}/{scenario.Steps.Count}";
            string commandLine = Scenario.Expand(step.Run, services.Ports);
            log.WriteLine("");
            log.WriteLine($"[{label}] {commandLine}");
            if (step.Description is not null)
            {
                log.WriteLine($"  ({step.Description})");
            }

            var watch = Stopwatch.StartNew();
            CommandResult result = Execute(Scenario.Split(commandLine), folder, environment);
            log.WriteLine($"  exit {result.ExitCode} (expected {step.ExitCode}) after {watch.Elapsed.TotalSeconds:F1} s");
            log.WriteLine(Indent(result.Tail(6000)));

            // Each message names where it failed and shows the output, so a CI log alone is
            // enough to see why.
            string where = $"{scenario.Name}, {label}: {commandLine}";
            Assert.True(result.ExitCode == step.ExitCode,
                $"{where}{Environment.NewLine}exited {result.ExitCode}, expected {step.ExitCode}{Environment.NewLine}{result.Tail()}");
            foreach (string expected in step.Stdout.Select(text => Scenario.Expand(text, services.Ports)))
            {
                Assert.True(result.StdOut.Contains(expected, StringComparison.Ordinal),
                    $"{where}{Environment.NewLine}its standard output lacks \"{expected}\"{Environment.NewLine}{result.Tail()}");
            }

            foreach (string file in step.Files.Select(text => Scenario.Expand(text, services.Ports)))
            {
                string path = Path.GetFullPath(Path.Combine(folder, file));
                Assert.True(File.Exists(path) || Directory.Exists(path),
                    $"{where}{Environment.NewLine}{file} does not exist after the step; {Listing(path, folder)}");
            }
        }
    }

    /// <summary>
    /// Makes the scenario's work folder: the shared files of tests/scenarios into
    /// artifacts/scenarios, then a fresh copy of the scenario's folder (an earlier run's copy is
    /// deleted first), without build outputs or IDE state.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The work folder: artifacts/scenarios/&lt;Name&gt;.</returns>
    /// <exception cref="IOException">A file is in use (a program of an earlier run still running) or cannot be copied.</exception>
    /// <exception cref="UnauthorizedAccessException">A file of the earlier copy may not be deleted.</exception>
    private static string Prepare(Scenario scenario)
    {
        Directory.CreateDirectory(WorkRoot);

        // Directory.Build.props, Directory.Packages.props, global.json, nuget.config: what makes
        // the folder a repository of its own (MSBuild and dotnet search upward for them, and
        // must stop here rather than at the AsmSdk repository's own files).
        foreach (string file in Directory.GetFiles(Repo.Scenarios))
        {
            File.Copy(file, Path.Combine(WorkRoot, Path.GetFileName(file)), overwrite: true);
        }

        string target = Path.Combine(WorkRoot, scenario.Name);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        CopyFolder(scenario.SourceFolder, target);
        return target;
    }

    /// <summary>Copies a folder and everything in it, except the folders in <see cref="SkippedFolders"/>.</summary>
    /// <param name="source">The folder to copy.</param>
    /// <param name="target">The new folder (created).</param>
    /// <exception cref="IOException">A file cannot be copied.</exception>
    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string folder in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(folder);
            if (!SkippedFolders.Contains(name))
            {
                CopyFolder(folder, Path.Combine(target, name));
            }
        }
    }

    /// <summary>
    /// Starts a step's command: <c>dotnet</c> and names on PATH as they are, a path (it holds a
    /// slash) as a file of the work folder. On Windows a path without an extension gets .exe,
    /// .cmd or .bat, the first that exists, as a shell would find it, and .cmd or .bat files run
    /// through cmd.exe, which is what runs them.
    /// </summary>
    /// <param name="words">The program, then its arguments.</param>
    /// <param name="folder">The work folder: the working directory, and the base of relative paths.</param>
    /// <param name="environment">The variables to set (the test environment).</param>
    /// <returns>The command's result; exit status -2 with the reason when it could not start (a missing program).</returns>
    private static CommandResult Execute(IReadOnlyList<string> words, string folder, IReadOnlyDictionary<string, string?> environment)
    {
        string program = words[0];
        string[] arguments = [.. words.Skip(1)];
        if (program.Contains('/') || program.Contains('\\'))
        {
            program = Path.GetFullPath(Path.Combine(folder, program));
            if (OperatingSystem.IsWindows() && !File.Exists(program) && Path.GetExtension(program).Length == 0)
            {
                program = new[] { ".exe", ".cmd", ".bat" }.Select(extension => program + extension).FirstOrDefault(File.Exists) ?? program;
            }
        }

        try
        {
            if (OperatingSystem.IsWindows() && Path.GetExtension(program).ToLowerInvariant() is ".cmd" or ".bat")
            {
                // cmd /s /c "<line>": /s takes the outer quotes off and runs the rest as typed, so
                // the quoted program and arguments inside keep their quotes. /d skips AutoRun
                // commands of the registry. ArgumentList cannot express this (it would quote the
                // whole line again), hence a raw command line.
                string line = string.Join(' ', arguments.Prepend(program).Select(QuoteForWindows));
                return Command.RunCommandLine("cmd.exe", $"/d /s /c \"{line}\"", folder, environment);
            }

            return Command.Run(program, arguments, folder, environment);
        }
        catch (Win32Exception e)
        {
            // Not found or not executable: a scenario failure to report like any other, with the
            // program's resolved path, rather than an exception that hides which step it was.
            return new CommandResult(-2, "", $"cannot start {program}: {e.Message}");
        }
    }

    /// <summary>
    /// Quotes one argument for a Windows command line the way CommandLineToArgvW and the C
    /// runtime read it back: unchanged when it needs nothing, else in double quotes, with the
    /// backslashes before a quote (and before the closing quote) doubled and inner quotes escaped.
    /// </summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The quoted form.</returns>
    private static string QuoteForWindows(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless a quote follows them: then each must be doubled,
            // and the quote itself escaped.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>What the folder that should hold a missing file holds, for the failure message.</summary>
    /// <param name="path">The missing file's full path.</param>
    /// <param name="folder">The work folder, which the listing's paths are relative to.</param>
    /// <returns>A sentence listing the nearest existing folder's files (at most 40).</returns>
    private static string Listing(string path, string folder)
    {
        string? parent = Path.GetDirectoryName(path);
        while (parent is not null && !Directory.Exists(parent))
        {
            parent = Path.GetDirectoryName(parent);
        }

        if (parent is null)
        {
            return "no folder on its path exists";
        }

        string[] files = Directory.GetFiles(parent, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        string count = files.Length == 1 ? "1 file" : $"{files.Length} files";
        return $"{Path.GetRelativePath(folder, parent).Replace('\\', '/')}/ holds {count}: {string.Join(", ", files.Take(40))}{(files.Length > 40 ? ", ..." : "")}";
    }

    /// <summary>Indents every line of a command's output for the log, so the commands stand out.</summary>
    /// <param name="text">The output.</param>
    /// <returns>The indented text; "(no output)" when it is empty.</returns>
    private static string Indent(string text) =>
        text.Trim().Length == 0
            ? "    (no output)"
            : string.Join(Environment.NewLine, text.TrimEnd().Split('\n').Select(line => "    " + line.TrimEnd('\r')));

    /// <summary>
    /// Probes, once per platform, whether Docker here runs Linux containers of that platform:
    /// Docker on PATH, a Linux engine (Windows runners have Windows containers only), and an
    /// alpine container of the platform that starts (ARM64 hosts without x64 emulation fail).
    /// </summary>
    /// <param name="platform">The container platform, such as linux/amd64.</param>
    /// <returns>Null when it can; otherwise why not.</returns>
    private static string? DockerUnavailable(string platform) =>
        DockerProbes.GetOrAdd(platform, key => new Lazy<string?>(() =>
        {
            if (!Command.OnPath("docker"))
            {
                return "Docker is not installed here";
            }

            CommandResult info = Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root);
            string engine = info.StdOut.Trim();
            if (info.ExitCode != 0)
            {
                return "Docker is not running here";
            }

            if (engine != "linux")
            {
                return $"Docker here runs {engine} containers, not Linux ones";
            }

            return Command.Run("docker", ["run", "--rm", "--platform", key, "alpine:3", "true"], Repo.Root).ExitCode == 0
                ? null
                : $"Docker here cannot run {key} containers";
        })).Value;

    /// <summary>
    /// The running service containers of one scenario run: started in order, each published on
    /// a free port of 127.0.0.1, and all removed by <see cref="Dispose"/>.
    /// </summary>
    private sealed class ScenarioServices : IDisposable
    {
        /// <summary>The ids of the containers started so far, removed by <see cref="Dispose"/>.</summary>
        private readonly List<string> _containers = [];

        /// <summary>The host port of each started service, by service name.</summary>
        private readonly Dictionary<string, int> _ports = new(StringComparer.Ordinal);

        /// <summary>The host port of each started service, by name: what <c>{port:name}</c> expands to.</summary>
        public IReadOnlyDictionary<string, int> Ports => _ports;

        /// <summary>Starts a scenario's services and waits until each is ready.</summary>
        /// <param name="scenario">The scenario.</param>
        /// <param name="log">The test's log: each docker command.</param>
        /// <returns>The running services (none when the scenario declares none).</returns>
        /// <exception cref="InvalidOperationException">docker run failed, the published port could not be read, or a container stopped before it was ready.</exception>
        /// <exception cref="TimeoutException">A service did not become ready within two minutes.</exception>
        public static ScenarioServices Start(Scenario scenario, ITestOutputHelper log)
        {
            var services = new ScenarioServices();
            try
            {
                foreach (ScenarioService service in scenario.Services)
                {
                    services.StartOne(service, log);
                }

                return services;
            }
            catch
            {
                // The ones already started must not outlive the failed run.
                services.Dispose();
                throw;
            }
        }

        /// <summary>Removes every container started (they also run with --rm); errors are ignored, as a container may be gone already.</summary>
        public void Dispose()
        {
            foreach (string id in _containers)
            {
                Command.Run("docker", ["rm", "-f", id], Repo.Root);
            }

            _containers.Clear();
        }

        /// <summary>Starts one service, reads its host port, and waits until it is ready.</summary>
        /// <param name="service">The service.</param>
        /// <param name="log">The test's log.</param>
        /// <exception cref="InvalidOperationException">docker run or docker port failed, or the container stopped before it was ready.</exception>
        /// <exception cref="TimeoutException">The service did not become ready in time.</exception>
        private void StartOne(ScenarioService service, ITestOutputHelper log)
        {
            // 127.0.0.1:: publishes on a free host port, so parallel runs and a developer's own
            // server on the default port never collide.
            List<string> arguments = ["run", "-d", "--rm", "--platform", service.Platform, "-p", $"127.0.0.1::{service.Port}"];
            foreach ((string name, string value) in service.Environment)
            {
                arguments.AddRange(["-e", $"{name}={value}"]);
            }

            arguments.Add(service.Image);
            log.WriteLine($"[service {service.Name}] docker {string.Join(' ', arguments)}");
            CommandResult run = Command.Run("docker", arguments, Repo.Root);
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException($"docker run of the {service.Name} service failed (exit {run.ExitCode}): {run.Tail()}");
            }

            // A pull prints its progress on standard error; the container id is the one line of
            // standard output.
            string id = run.StdOut.Trim();
            _containers.Add(id);
            CommandResult port = Command.Run("docker", ["port", id, $"{service.Port}/tcp"], Repo.Root);
            string? published = port.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (port.ExitCode != 0 || published is null
                || !int.TryParse(published[(published.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture, out int hostPort))
            {
                throw new InvalidOperationException($"no host port for the {service.Name} service's {service.Port}/tcp: {port.Tail()}");
            }

            _ports[service.Name] = hostPort;
            WaitUntilReady(service, id, hostPort);
            log.WriteLine($"[service {service.Name}] ready on 127.0.0.1:{hostPort}");
        }

        /// <summary>
        /// Polls until the service is ready: its log shows <see cref="ScenarioService.Ready"/>,
        /// or, without one, its port accepts a TCP connection.
        /// </summary>
        /// <param name="service">The service.</param>
        /// <param name="id">Its container id.</param>
        /// <param name="hostPort">Its host port.</param>
        /// <exception cref="InvalidOperationException">The container stopped (its log can no longer be read).</exception>
        /// <exception cref="TimeoutException">Not ready within <see cref="ReadyTimeout"/>; the message holds the log's tail.</exception>
        private static void WaitUntilReady(ScenarioService service, string id, int hostPort)
        {
            var watch = Stopwatch.StartNew();
            string lastLog = "";
            while (watch.Elapsed < ReadyTimeout)
            {
                if (service.Ready is not null)
                {
                    CommandResult logs = Command.Run("docker", ["logs", id], Repo.Root);
                    if (logs.ExitCode != 0)
                    {
                        // --rm removed it when it exited: there is nothing left to wait for.
                        throw new InvalidOperationException($"the {service.Name} container stopped before it was ready: {logs.Tail()}");
                    }

                    lastLog = logs.Output;
                    if (lastLog.Contains(service.Ready, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                else if (CanConnect(hostPort))
                {
                    return;
                }

                Thread.Sleep(250);
            }

            throw new TimeoutException($"the {service.Name} service was not ready after {ReadyTimeout.TotalSeconds:F0} s; its log: {lastLog}");
        }

        /// <summary>Whether 127.0.0.1:port accepts a TCP connection now.</summary>
        /// <param name="port">The port.</param>
        /// <returns>True when a connection was made (and closed).</returns>
        private static bool CanConnect(int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
