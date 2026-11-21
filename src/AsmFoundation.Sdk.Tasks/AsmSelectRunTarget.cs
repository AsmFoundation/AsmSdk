using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// dotnet run of an asm program: picks the build this machine can run and says how to start it
/// (RunCommand and RunArguments, which the .NET CLI reads after ComputeRunArguments).
/// </summary>
/// <remarks>
/// <para>
/// An asm program is built for the RIDs its project lists, and the machine running dotnet run
/// may be none of them. The order, the owner's choice for AsmSdk (2026-11-21):
/// </para>
/// <list type="number">
/// <item>this machine's own RID, then another build it runs natively: on Linux one of its CPU
/// whose loader exists here (a static freestanding program needs none, so linux-x64 and
/// linux-musl-x64 both run on either C library);</item>
/// <item>one it emulates: win-x64 on Windows on Arm, osx-x64 on Apple silicon with Rosetta 2;</item>
/// <item>Windows only: a Linux build of this machine's CPU through WSL, when the default (or the
/// named) distribution has the program's loader;</item>
/// <item>a Linux build in a Docker container: debian:stable-slim for glibc, alpine:3 for musl,
/// another CPU under the emulation Docker Desktop includes (QEMU).</item>
/// </list>
/// <para>
/// AsmRunIn narrows the choice to one of Host (1 and 2), Wsl or Docker. The program's own
/// arguments (RunArguments of the project, and what follows "--" on the command line, which the
/// CLI appends) reach the program unchanged in every mode.
/// </para>
/// <para>
/// Footguns: a program run through Docker sees its own folder as /app and works there, not in
/// the folder dotnet run was started from, and the environment variables of a launch profile
/// reach the docker command, not the container. Through WSL the working directory is the
/// current one, as WSL maps it. A Linux machine needs binfmt_misc emulation (QEMU) for Docker to
/// run another CPU's build; Docker Desktop has it.
/// </para>
/// <para>
/// Errors: ASM0091 (no build this machine can run, with what was
/// tried), ASM0092 (nothing was built: dotnet run --no-build before a build), ASM0093 (unknown
/// AsmRunIn). ASM0090, a project that builds no program, is the targets' own check.
/// </para>
/// </remarks>
public sealed class AsmSelectRunTarget : Task
{
    /// <summary>
    /// The program of each RID the project built: the item is the program's full path, with
    /// RuntimeIdentifier, AsmOs (Windows, Linux, MacOS), AsmArch (x64, arm64), AsmMusl ("true" for
    /// a musl RID) and AsmDynamicLinker (the loader of a Linux libc program; empty for a static one).
    /// </summary>
    [Required]
    public ITaskItem[] Programs { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Where to run: Auto (the order above, the default), Host, Wsl or Docker.</summary>
    public string RunIn { get; set; } = "";

    /// <summary>The WSL distribution to run in; empty for WSL's default one.</summary>
    public string WslDistribution { get; set; } = "";

    /// <summary>The Docker image to run in; empty for debian:stable-slim (glibc) or alpine:3 (musl).</summary>
    public string DockerImage { get; set; } = "";

    /// <summary>Whether this machine's C library is musl (the .NET SDK's own RID says so).</summary>
    public bool HostIsMusl { get; set; }

    /// <summary>The project's own program arguments (RunArguments or StartArguments), appended as written.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>The project name, for messages.</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>The project file, for messages.</summary>
    public string ProjectFile { get; set; } = "";

    /// <summary>The program to start: the program itself, wsl.exe or docker.</summary>
    [Output]
    public string RunCommand { get; set; } = "";

    /// <summary>Its arguments, before those the CLI appends.</summary>
    [Output]
    public string RunArguments { get; set; } = "";

    /// <summary>The RID of the build that runs.</summary>
    [Output]
    public string SelectedRuntimeIdentifier { get; set; } = "";

    /// <summary>How it runs: Host, Emulation, Wsl or Docker.</summary>
    [Output]
    public string RunsIn { get; set; } = "";

    /// <summary>How long a probe of WSL or Docker may take: a cold WSL virtual machine starts in seconds, not minutes.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Selects the build and the way to run it.</summary>
    /// <returns>True with the outputs set; false after logging ASM0091, ASM0092 or ASM0093.</returns>
    public override bool Execute()
    {
        string mode = RunIn.Trim();
        string[] modes = { "Auto", "Host", "Wsl", "Docker" };
        string? known = mode.Length == 0 ? "Auto" : modes.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            Log.LogError(null, "ASM0093", null, ProjectFile, 0, 0, 0, 0,
                "AsmRunIn '{0}' is not a way to run a program. Use Auto (the default: this machine, then WSL, then Docker), Host, Wsl or Docker.", RunIn);
            return false;
        }

        // Builds that exist: dotnet run builds first, so a missing file means --no-build before
        // any build (or a build that failed for that RID, which dotnet run already reported).
        var built = Programs.Where(p => File.Exists(p.ItemSpec)).ToList();
        if (built.Count == 0)
        {
            Log.LogError(null, "ASM0092", null, ProjectFile, 0, 0, 0, 0,
                "{0} has no built program to run ({1}). Build it first (dotnet build), or run without --no-build.",
                ProjectName, Programs.Length == 0 ? "it builds no RID" : string.Join(", ", Programs.Select(p => p.ItemSpec)));
            return false;
        }

        string hostOs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "MacOS" : "Linux";
        // The OS's CPU, not this process's: an x64 MSBuild under emulation on Windows on Arm
        // still runs on a machine that runs arm64 programs natively.
        string hostArch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        // This machine as a RID, the way messages and RuntimeIdentifiers spell one.
        string hostRid = hostOs switch
        {
            "Windows" => "win-" + hostArch,
            "MacOS" => "osx-" + hostArch,
            _ => (HostIsMusl ? "linux-musl-" : "linux-") + hostArch,
        };
        var tried = new List<string>();

        if (known == "Auto" || known == "Host")
        {
            if (TryHost(built, hostOs, hostArch, hostRid, tried))
            {
                return true;
            }
        }

        if ((known == "Auto" || known == "Wsl") && hostOs == "Windows")
        {
            if (TryWsl(built, hostArch, tried))
            {
                return true;
            }
        }
        else if (known == "Wsl")
        {
            tried.Add("WSL exists on Windows only");
        }

        if (known == "Auto" || known == "Docker")
        {
            if (TryDocker(built, hostArch, tried))
            {
                return true;
            }
        }

        Log.LogError(null, "ASM0091", null, ProjectFile, 0, 0, 0, 0,
            "This machine ({0}) cannot run any build of {1} ({2}): {3}. Build a RID this machine runs (dotnet run -r <rid>, with the RID in RuntimeIdentifiers), install WSL with a Linux distribution, or start Docker.",
            hostRid, ProjectName, string.Join(", ", built.Select(p => p.GetMetadata("RuntimeIdentifier"))), string.Join("; ", tried));
        return false;
    }

    /// <summary>Steps 1 and 2: a build this machine runs itself, natively or under its own emulation.</summary>
    /// <param name="built">The builds that exist.</param>
    /// <param name="hostOs">This machine's OS (Windows, Linux, MacOS).</param>
    /// <param name="hostArch">This machine's CPU (x64, arm64).</param>
    /// <param name="hostRid">This machine as a RID, for messages.</param>
    /// <param name="tried">What was tried, for ASM0091 (appended to).</param>
    /// <returns>True when one was selected.</returns>
    private bool TryHost(List<ITaskItem> built, string hostOs, string hostArch, string hostRid, List<string> tried)
    {
        // The machine's own flavor first (its C library on Linux), so that a project building
        // both linux-x64 and linux-musl-x64 runs the one that matches the machine.
        var native = built
            .Where(p => p.GetMetadata("AsmOs") == hostOs && p.GetMetadata("AsmArch") == hostArch)
            .OrderBy(p => (p.GetMetadata("AsmMusl") == "true") == HostIsMusl ? 0 : 1)
            .ToList();
        foreach (ITaskItem program in native)
        {
            string loader = program.GetMetadata("AsmDynamicLinker");
            if (hostOs == "Linux" && loader.Length != 0 && !File.Exists(loader))
            {
                tried.Add($"{program.GetMetadata("RuntimeIdentifier")} needs {loader}, which this machine does not have");
                continue;
            }

            Select(program, "Host", program.ItemSpec, Arguments);
            return true;
        }

        // Emulation the OS provides itself: Windows on Arm runs x64 programs, and Apple silicon
        // runs them through Rosetta 2 once it is installed (its runtime file says so). Linux
        // has none by default (binfmt_misc with QEMU is a setup of its own; Docker uses it).
        bool emulates = hostArch == "arm64" && hostOs != "Linux";
        ITaskItem? x64 = emulates ? built.FirstOrDefault(p => p.GetMetadata("AsmOs") == hostOs && p.GetMetadata("AsmArch") == "x64") : null;
        if (x64 is not null)
        {
            if (hostOs == "Windows" || File.Exists("/Library/Apple/usr/libexec/oah/libRosettaRuntime"))
            {
                Select(x64, "Emulation", x64.ItemSpec, Arguments);
                return true;
            }

            tried.Add("osx-x64 needs Rosetta 2 (softwareupdate --install-rosetta)");
        }
        else if (native.Count == 0)
        {
            // Only when nothing of this machine's kind was there to try: the loader and Rosetta
            // messages above say more when something was.
            tried.Add($"no build runs on this machine itself ({hostRid}{(emulates ? ", or x64 under its emulation" : "")})");
        }

        return false;
    }

    /// <summary>Step 3: a Linux build of this machine's CPU, through WSL.</summary>
    /// <param name="built">The builds that exist.</param>
    /// <param name="hostArch">This machine's CPU.</param>
    /// <param name="tried">What was tried, for ASM0091 (appended to).</param>
    /// <returns>True when one was selected.</returns>
    /// <remarks>
    /// One wsl.exe call per candidate answers both questions at once: whether the distribution
    /// has the program's loader (glibc's or musl's; a static program needs none) and what the
    /// program's path is inside WSL (wslpath, which knows the distribution's mount root). The
    /// loader travels as "-" when there is none: wsl.exe refuses an empty argument
    /// (Wsl/Service/E_INVALIDARG, measured with WSL 2 on Windows 11).
    /// </remarks>
    private bool TryWsl(List<ITaskItem> built, string hostArch, List<string> tried)
    {
        string wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        var candidates = built.Where(p => p.GetMetadata("AsmOs") == "Linux" && p.GetMetadata("AsmArch") == hostArch)
            .OrderBy(p => p.GetMetadata("AsmMusl") == "true" ? 1 : 0).ToList();
        if (candidates.Count == 0)
        {
            tried.Add($"WSL runs linux-{hostArch} or linux-musl-{hostArch} builds, and there is none");
            return false;
        }

        if (!File.Exists(wsl))
        {
            tried.Add("WSL is not installed");
            return false;
        }

        string distribution = WslDistribution.Trim().Length == 0 ? "" : "-d " + Quote(WslDistribution.Trim()) + " ";
        const string Script = "if [ \"$1\" != - ] && [ ! -e \"$1\" ]; then exit 42; fi; exec wslpath -a -u \"$2\"";
        foreach (ITaskItem program in candidates)
        {
            string loader = program.GetMetadata("AsmDynamicLinker");
            ProbeResult probe = Probe(wsl, distribution + "--exec sh -c " + Quote(Script) + " sh " + Quote(loader.Length == 0 ? "-" : loader) + " " + Quote(program.ItemSpec));
            string rid = program.GetMetadata("RuntimeIdentifier");
            if (probe.ExitCode == 42)
            {
                tried.Add($"WSL{(WslDistribution.Length == 0 ? "" : " (" + WslDistribution + ")")} has no {loader} for {rid}");
                continue;
            }

            string linuxPath = probe.Output.Trim();
            if (probe.ExitCode != 0 || !linuxPath.StartsWith("/", StringComparison.Ordinal))
            {
                // No distribution, WSL not set up, or the path is not reachable from WSL.
                tried.Add($"WSL did not run ({probe.Describe()})");
                return false;
            }

            Select(program, "Wsl", wsl, distribution + "--exec " + Quote(linuxPath) + Append(Arguments));
            return true;
        }

        return false;
    }

    /// <summary>Step 4: a Linux build in a Docker container, any CPU.</summary>
    /// <param name="built">The builds that exist.</param>
    /// <param name="hostArch">This machine's CPU (its own builds go first: no emulation).</param>
    /// <param name="tried">What was tried, for ASM0091 (appended to).</param>
    /// <returns>True when one was selected.</returns>
    /// <remarks>
    /// The program's folder is mounted at /app, its working directory, so the libraries the
    /// build copied next to (or below) it come along. The docker command is looked up on PATH
    /// here and started by its full path. Docker Desktop on Windows in Windows-container mode
    /// counts as no Docker (the server's OS must be linux).
    /// </remarks>
    private bool TryDocker(List<ITaskItem> built, string hostArch, List<string> tried)
    {
        var candidates = built.Where(p => p.GetMetadata("AsmOs") == "Linux")
            .OrderBy(p => p.GetMetadata("AsmArch") == hostArch ? 0 : 1)
            .ThenBy(p => p.GetMetadata("AsmMusl") == "true" ? 1 : 0).ToList();
        if (candidates.Count == 0)
        {
            tried.Add("Docker runs Linux builds, and there is none");
            return false;
        }

        string? docker = FindOnPath("docker");
        if (docker is null)
        {
            tried.Add("Docker is not installed (no docker on PATH)");
            return false;
        }

        ProbeResult probe = Probe(docker, "version --format \"{{.Server.Os}}\"");
        if (probe.ExitCode != 0 || probe.Output.Trim() != "linux")
        {
            tried.Add(probe.ExitCode == 0 ? $"Docker runs {probe.Output.Trim()} containers, not Linux ones" : $"Docker is not running ({probe.Describe()})");
            return false;
        }

        ITaskItem program = candidates[0];
        bool musl = program.GetMetadata("AsmMusl") == "true";
        string image = DockerImage.Trim().Length != 0 ? DockerImage.Trim() : musl ? "alpine:3" : "debian:stable-slim";
        string platform = program.GetMetadata("AsmArch") == "arm64" ? "linux/arm64" : "linux/amd64";
        string folder = Path.GetDirectoryName(program.ItemSpec)!;
        string arguments = $"run --rm -i --platform {platform} -v {Quote(folder + ":/app")} -w /app {Quote(image)} {Quote("/app/" + Path.GetFileName(program.ItemSpec))}{Append(Arguments)}";
        Select(program, "Docker", docker, arguments);
        return true;
    }

    /// <summary>Sets the outputs and says, at high importance, what runs and how.</summary>
    /// <param name="program">The selected build.</param>
    /// <param name="how">Host, Emulation, Wsl or Docker.</param>
    /// <param name="command">RunCommand.</param>
    /// <param name="arguments">RunArguments.</param>
    private void Select(ITaskItem program, string how, string command, string arguments)
    {
        RunCommand = command;
        RunArguments = arguments;
        SelectedRuntimeIdentifier = program.GetMetadata("RuntimeIdentifier");
        RunsIn = how;
        string where = how switch
        {
            "Emulation" => " under this machine's x64 emulation",
            "Wsl" => " through WSL" + (WslDistribution.Trim().Length == 0 ? "" : " (" + WslDistribution.Trim() + ")"),
            "Docker" => " in a Docker container",
            _ => "",
        };
        Log.LogMessage(MessageImportance.High, "{0}: running the {1} build{2}: {3}", ProjectName, SelectedRuntimeIdentifier, where, program.ItemSpec);
    }

    /// <summary>The project's arguments with a leading space, or nothing.</summary>
    /// <param name="arguments">Arguments as the project wrote them.</param>
    /// <returns>" " + arguments, or "".</returns>
    private static string Append(string arguments) => arguments.Trim().Length == 0 ? "" : " " + arguments.Trim();

    /// <summary>What a probe printed and returned.</summary>
    private readonly struct ProbeResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="exitCode">The exit code (-1 when the program could not start or timed out).</param>
        /// <param name="output">Standard output.</param>
        /// <param name="error">Standard error, or why the program did not run.</param>
        public ProbeResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output;
            Error = error;
        }

        /// <summary>The exit code (-1 when the program could not start or timed out).</summary>
        public int ExitCode { get; }

        /// <summary>Standard output.</summary>
        public string Output { get; }

        /// <summary>Standard error, or why the program did not run.</summary>
        public string Error { get; }

        /// <summary>A short account for messages: the exit code and the first line of what it said.</summary>
        /// <returns>For example "exit code 1: no such distribution".</returns>
        public string Describe()
        {
            // wsl.exe prints its own messages in UTF-16, which read as UTF-8 come out with a NUL
            // between letters: drop those so the message stays readable.
            string said = (Error.Trim().Length != 0 ? Error : Output).Replace("\0", "").Trim();
            string first = said.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return first.Length == 0 ? $"exit code {ExitCode}" : $"exit code {ExitCode}: {first}";
        }
    }

    /// <summary>Runs a probe command to completion, with a timeout.</summary>
    /// <param name="fileName">The program.</param>
    /// <param name="arguments">Its command line, already quoted.</param>
    /// <returns>The result; ExitCode -1 when it could not start or did not finish in time (it is then killed).</returns>
    private static ProbeResult Probe(string fileName, string arguments)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using Process process = Process.Start(start)!;
            // Nothing to read from us: an inherited console stdin would let wsl.exe or docker
            // wait on the user's keyboard.
            process.StandardInput.Close();

            // Both streams are read while the process runs: reading one to its end first
            // deadlocks once the other fills its pipe.
            System.Threading.Tasks.Task<string> output = process.StandardOutput.ReadToEndAsync();
            System.Threading.Tasks.Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // It exited between the wait and the kill: nothing left to stop.
                }

                return new ProbeResult(-1, "", $"no answer within {ProbeTimeout.TotalSeconds} s");
            }

            process.WaitForExit();
            return new ProbeResult(process.ExitCode, output.Result, error.Result);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ProbeResult(-1, "", ex.Message);
        }
    }

    /// <summary>The full path of a command found on PATH, as a shell would find it.</summary>
    /// <param name="name">The command's name, without an extension.</param>
    /// <returns>Its full path, or null.</returns>
    private static string? FindOnPath(string name)
    {
        string[] extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { ".exe", ".cmd" } : new[] { "" };
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(folder.Trim().Trim('"'), name + extension);
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters no path may hold: skip it, as a shell would.
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One argument quoted for a Windows command line (the rules CommandLineToArgvW and the C
    /// runtime read, which wsl.exe and docker follow, and which .NET applies on Unix too): as is
    /// when it has no space, tab or quote; otherwise in double quotes, a quote escaped with a
    /// backslash, and the backslashes before a quote (or the closing one) doubled.
    /// </summary>
    /// <param name="argument">The argument; never empty (wsl.exe refuses an empty one).</param>
    /// <returns>The quoted argument.</returns>
    internal static string Quote(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
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

            if (c == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled, so that they stay backslashes.
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}
