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
/// <item>the wasi-wasm build, a WASI command, in wasmtime (found on PATH), on any machine
/// (added with WebAssembly support, after the builds the machine runs itself: a native build is
/// what a project that also builds one means to run here);</item>
/// <item>Windows only: a Linux build of this machine's CPU through WSL, when the default (or the
/// named) distribution has the program's loader;</item>
/// <item>a Linux build in a Docker container: debian:stable-slim for glibc, alpine:3 for musl,
/// another CPU under the emulation Docker Desktop includes (QEMU).</item>
/// </list>
/// <para>
/// AsmRunIn narrows the choice to one of Host (1 and 2), Wasi (3), Wsl or Docker. The program's
/// own arguments (RunArguments of the project, and what follows "--" on the command line, which
/// the CLI appends) reach the program unchanged in every mode.
/// </para>
/// <para>
/// A WASI program gets the folder dotnet run was started in as its current directory ("."),
/// so that it can open files there as a native program would; wasmtime grants a WASI program
/// no file access at all otherwise.
/// </para>
/// <para>
/// Environment variables: the CLI sets dotnet run -e's and the launch profile's on the process it
/// starts, which is wsl.exe, docker or wasmtime here, and none of the three passes its own
/// environment on (measured: "-e ASM_PROBE=x" reached a program through WSL as unset). Each is
/// told the names, never the values, which stay in that process's environment: wasmtime
/// "--env NAME" and docker "-e NAME" inherit the variable, and pass nothing for a name the
/// environment lacks (measured: wasmtime 39, Docker 28); WSL takes "NAME/u" entries from WSLENV,
/// which this task sets on wsl.exe through the CLI (<see cref="EnvironmentVariablesToSet"/>).
/// wasmtime and Docker get the names of -e (<see cref="EnvironmentVariables"/>) and of every
/// profile in the launch settings file (<see cref="LaunchSettingsFile"/>), whichever profile
/// runs. WSL gets -e's alone: a name WSLENV lists that the environment lacks reaches the Linux
/// program as an EMPTY variable (measured, WSL 2), and the names of a profile that does not run
/// would be just that. PATH is never passed: the host's would replace the program's own.
/// Footguns: a launch profile's variables never reach a program run through WSL; and the .NET 10
/// SDK's CLI passes -e to no build and applies no item, so there only launch profiles' variables
/// reach Docker and wasmtime, and nothing reaches WSL unless WSLENV already names it.
/// </para>
/// <para>
/// Footguns: a program run through Docker sees its own folder as /app and works there, not in
/// the folder dotnet run was started from. Through WSL the working directory is the current one,
/// as WSL maps it. A Linux machine needs binfmt_misc emulation (QEMU) for Docker to run another
/// CPU's build; Docker Desktop has it. A launch profile's commandLineArgs never reach a program
/// run through wasmtime, WSL or Docker: the CLI applies them only to a command with no arguments
/// of its own (RunCommand.cs and CommonRunHelpers.CombineRunArguments, .NET 10 and 11 SDKs), and
/// these three have theirs; arguments after "--" do reach it.
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
    /// RuntimeIdentifier, AsmOs (Windows, Linux, MacOS, Wasi), AsmArch (x64, arm64, wasm),
    /// AsmMusl ("true" for a musl RID) and AsmDynamicLinker (the loader of a Linux libc program;
    /// empty for a static one).
    /// </summary>
    [Required]
    public ITaskItem[] Programs { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Where to run: Auto (the order above, the default), Host, Wasi, Wsl or Docker.</summary>
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

    /// <summary>
    /// The variables of dotnet run -e: the .NET 11 SDK's CLI passes them as RuntimeEnvironmentVariable
    /// items (the item is the name, Value metadata the value) to a project that declares the
    /// RuntimeEnvironmentVariableSupport capability, as an asm program does. Empty with the .NET 10
    /// SDK's CLI, which passes -e to no build.
    /// </summary>
    public ITaskItem[] EnvironmentVariables { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The launch settings file dotnet run reads (Properties/launchSettings.json, else
    /// &lt;project&gt;.run.json, as the CLI looks for them); empty when there is none. The names
    /// of every profile's environmentVariables are passed on to wasmtime and Docker, whichever
    /// profile runs: a name the running profile does not set has no value, and both then pass
    /// nothing (WSL would pass an empty variable, so it gets none of these). A file this task cannot read (not JSON
    /// even with comments and trailing commas removed) only stops that, with a low-importance
    /// message: dotnet run itself reports a broken launch settings file.
    /// </summary>
    public string LaunchSettingsFile { get; set; } = "";

    /// <summary>
    /// Variables for the CLI to set on the started process, as RuntimeEnvironmentVariable items (the
    /// item is the name, Value metadata the value): WSLENV, when the program runs through WSL and
    /// variables must reach it. The .NET 11 SDK's CLI applies the RuntimeEnvironmentVariable items
    /// after ComputeRunArguments (EnvironmentVariablesToMSBuild.ReadFromItems); the .NET 10 SDK's
    /// ignores them.
    /// </summary>
    [Output]
    public ITaskItem[] EnvironmentVariablesToSet { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The names to pass on to wasmtime or Docker: -e's and every launch profile's (set by
    /// <see cref="Execute"/>). Both pass nothing for a name their environment lacks.
    /// </summary>
    private List<string> _forwarded = new();

    /// <summary>
    /// The names to pass on to WSL: -e's alone, the variables certain to be set on wsl.exe (set by
    /// <see cref="Execute"/>). WSL gives a listed name the environment lacks to the program as an
    /// empty variable, so a launch profile's names, which may belong to a profile that does not
    /// run, are left out.
    /// </summary>
    private List<string> _forwardedToWsl = new();

    /// <summary>The program to start: the program itself, wasmtime, wsl.exe or docker.</summary>
    [Output]
    public string RunCommand { get; set; } = "";

    /// <summary>Its arguments, before those the CLI appends.</summary>
    [Output]
    public string RunArguments { get; set; } = "";

    /// <summary>The RID of the build that runs.</summary>
    [Output]
    public string SelectedRuntimeIdentifier { get; set; } = "";

    /// <summary>How it runs: Host, Emulation, Wasi, Wsl or Docker.</summary>
    [Output]
    public string RunsIn { get; set; } = "";

    /// <summary>How long a probe of WSL or Docker may take: a cold WSL virtual machine starts in seconds, not minutes.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Selects the build and the way to run it.</summary>
    /// <returns>True with the outputs set; false after logging ASM0091, ASM0092 or ASM0093.</returns>
    public override bool Execute()
    {
        string mode = RunIn.Trim();
        string[] modes = { "Auto", "Host", "Wasi", "Wsl", "Docker" };
        string? known = mode.Length == 0 ? "Auto" : modes.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            Log.LogError(null, "ASM0093", null, ProjectFile, 0, 0, 0, 0,
                "AsmRunIn '{0}' is not a way to run a program. Use Auto (the default: this machine, then a WASI runtime, then WSL, then Docker), Host, Wasi, Wsl or Docker.", RunIn);
            return false;
        }

        _forwarded = ForwardedVariableNames(includeLaunchProfiles: true);
        _forwardedToWsl = ForwardedVariableNames(includeLaunchProfiles: false);

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

        if (known == "Auto" || known == "Wasi")
        {
            if (TryWasi(built, tried))
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
            "This machine ({0}) cannot run any build of {1} ({2}): {3}. Build a RID this machine runs (dotnet run -r <rid>, with the RID in RuntimeIdentifiers), install wasmtime for a wasi-wasm build, install WSL with a Linux distribution, or start Docker.",
            hostRid, ProjectName, string.Join(", ", built.Select(p => p.GetMetadata("RuntimeIdentifier"))), string.Join("; ", tried));
        return false;
    }

    /// <summary>Step 3: the wasi-wasm build, a WASI command, in wasmtime.</summary>
    /// <param name="built">The builds that exist.</param>
    /// <param name="tried">What was tried, for ASM0091 (appended to).</param>
    /// <returns>True when it was selected.</returns>
    /// <remarks>
    /// wasmtime is the WASI runtime of the Bytecode Alliance, which also maintains WASI itself,
    /// and the one .NET's own WASI target runs with; it starts a command at <c>_start</c> and
    /// returns its <c>proc_exit</c> code as its own exit code (measured with wasmtime 39). Found
    /// on PATH, as dotnet run finds nothing else: a WASI runtime is the user's to install, the
    /// way Docker is. "--dir ." preopens the current folder (see the class remarks); the
    /// program's path is quoted for the CLI's command line like every other argument here.
    /// </remarks>
    private bool TryWasi(List<ITaskItem> built, List<string> tried)
    {
        ITaskItem? program = built.FirstOrDefault(p => p.GetMetadata("AsmOs") == "Wasi");
        if (program is null)
        {
            // Said only when the user asked for WASI: in Auto it would only add noise to a
            // project that builds no WebAssembly at all.
            if (RunIn.Trim().Equals("Wasi", StringComparison.OrdinalIgnoreCase))
            {
                tried.Add("AsmRunIn=Wasi runs the wasi-wasm build, and there is none");
            }

            return false;
        }

        string? wasmtime = FindOnPath("wasmtime");
        if (wasmtime is null)
        {
            tried.Add("wasi-wasm needs a WASI runtime, and wasmtime is not on PATH (https://wasmtime.dev)");
            return false;
        }

        // wasmtime's options come before the module: "--env NAME" inherits NAME from wasmtime's
        // own environment (wasmtime 39's help), where the CLI put it.
        Select(program, "Wasi", wasmtime, "run --dir . " + ForwardArguments("--env") + Quote(program.ItemSpec) + Append(Arguments));
        return true;
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
            SetWslEnvironment();
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
        // docker run's options come before the image: "-e NAME" without a value passes NAME from
        // docker's own environment, where the CLI put it, and skips a NAME that is not set.
        string arguments = $"run --rm -i --platform {platform} -v {Quote(folder + ":/app")} -w /app {ForwardArguments("-e")}{Quote(image)} {Quote("/app/" + Path.GetFileName(program.ItemSpec))}{Append(Arguments)}";
        Select(program, "Docker", docker, arguments);
        return true;
    }

    /// <summary>Sets the outputs and says, at high importance, what runs and how.</summary>
    /// <param name="program">The selected build.</param>
    /// <param name="how">Host, Emulation, Wasi, Wsl or Docker.</param>
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
            "Wasi" => " in wasmtime",
            "Wsl" => " through WSL" + (WslDistribution.Trim().Length == 0 ? "" : " (" + WslDistribution.Trim() + ")"),
            "Docker" => " in a Docker container",
            _ => "",
        };
        // The variables matter only where they must be passed on: on this machine the program
        // gets the CLI's environment as it is.
        List<string> passed = how switch
        {
            "Wasi" or "Docker" => _forwarded,
            "Wsl" => _forwardedToWsl,
            _ => new List<string>(),
        };
        string variables = passed.Count != 0 ? " (environment variables passed on: " + string.Join(", ", passed) + ")" : "";
        Log.LogMessage(MessageImportance.High, "{0}: running the {1} build{2}: {3}{4}", ProjectName, SelectedRuntimeIdentifier, where, program.ItemSpec, variables);
    }

    /// <summary>The project's arguments with a leading space, or nothing.</summary>
    /// <param name="arguments">Arguments as the project wrote them.</param>
    /// <returns>" " + arguments, or "".</returns>
    private static string Append(string arguments) => arguments.Trim().Length == 0 ? "" : " " + arguments.Trim();

    /// <summary>
    /// The names of the variables a program run through wasmtime, WSL or Docker must be handed
    /// explicitly: dotnet run -e's (<see cref="EnvironmentVariables"/>), then, when asked, every
    /// launch profile's (<see cref="LaunchSettingsFile"/>), each once, in that order.
    /// </summary>
    /// <param name="includeLaunchProfiles">
    /// Whether the launch profiles' names count: true for wasmtime and Docker, which pass nothing
    /// for an unset name; false for WSL, which would pass it as an empty variable.
    /// </param>
    /// <returns>The names; empty when there are none.</returns>
    /// <remarks>
    /// Left out: PATH, whose host value would replace the Linux or WASI program's own search path
    /// (WSL and Docker set one that fits the program's system); WSLENV, which is WSL's own list
    /// and not a variable for the program; and names no environment can hold or that these
    /// command lines would read as something else: empty, with '=' (docker would take the rest
    /// for a value), whitespace, ':' (WSLENV's separator) or '/' (WSLENV's flag mark). Names are
    /// compared without case on Windows, whose environment ignores it, and exactly elsewhere.
    /// </remarks>
    private List<string> ForwardedVariableNames(bool includeLaunchProfiles)
    {
        StringComparer comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var names = new List<string>();
        IEnumerable<string> candidates = EnvironmentVariables.Select(item => item.ItemSpec)
            .Concat(!includeLaunchProfiles || LaunchSettingsFile.Trim().Length == 0 ? Enumerable.Empty<string>() : LaunchProfileVariableNames(LaunchSettingsFile.Trim()));
        foreach (string candidate in candidates)
        {
            string name = candidate.Trim();
            bool usable = name.Length != 0
                && name.IndexOfAny(new[] { '=', ':', '/', ' ', '\t', '\r', '\n' }) < 0
                && !name.Equals("PATH", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("WSLENV", StringComparison.OrdinalIgnoreCase);
            if (usable && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The names of every profile's environmentVariables in a launch settings file.</summary>
    /// <param name="file">The launch settings file (launchSettings.json or a .run.json).</param>
    /// <returns>The names, in file order; empty when the file cannot be read or holds none.</returns>
    /// <remarks>
    /// The CLI reads launch settings with comments and trailing commas allowed, so they are taken
    /// out first (<see cref="StripJsonComments"/>); <see cref="Json"/> is strict. A file that still
    /// does not parse costs only the profiles' variables, said at low importance: dotnet run itself
    /// reports a launch settings file it cannot read, and the program still runs.
    /// </remarks>
    private List<string> LaunchProfileVariableNames(string file)
    {
        var names = new List<string>();
        JsonNode root;
        try
        {
            root = Json.Parse(StripJsonComments(File.ReadAllText(file)));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Log.LogMessage(MessageImportance.Low, "{0}: the launch profiles' environment variables are not passed on: {1} could not be read ({2}).", ProjectName, file, ex.Message);
            return names;
        }

        if (root is JsonObject settings && settings["profiles"] is JsonObject profiles)
        {
            foreach (KeyValuePair<string, JsonNode> profile in profiles.Members)
            {
                if (profile.Value is JsonObject body && body["environmentVariables"] is JsonObject variables)
                {
                    names.AddRange(variables.Members.Select(member => member.Key));
                }
            }
        }

        return names;
    }

    /// <summary>
    /// JSON with comments and trailing commas, as launch settings files may hold them (the CLI
    /// reads them that way), turned into strict JSON: "//" and "/* */" comments become whitespace
    /// and a comma before a closing brace or bracket goes, outside strings only.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The same document as strict JSON (an unterminated block comment runs to the end).</returns>
    internal static string StripJsonComments(string text)
    {
        // Pass 1: comments out. A string's characters are copied as they are, escapes included,
        // so a "//" inside a URL stays.
        var withoutComments = new StringBuilder(text.Length);
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                withoutComments.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    withoutComments.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                withoutComments.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                withoutComments.Append(' ');
                continue;
            }

            inString = c == '"';
            withoutComments.Append(c);
        }

        // Pass 2: a comma whose next character, past whitespace, closes an object or an array.
        string source = withoutComments.ToString();
        var strict = new StringBuilder(source.Length);
        inString = false;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (inString)
            {
                strict.Append(c);
                if (c == '\\' && i + 1 < source.Length)
                {
                    strict.Append(source[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == ',')
            {
                int next = i + 1;
                while (next < source.Length && char.IsWhiteSpace(source[next]))
                {
                    next++;
                }

                if (next < source.Length && (source[next] == '}' || source[next] == ']'))
                {
                    continue;
                }
            }

            inString = c == '"';
            strict.Append(c);
        }

        return strict.ToString();
    }

    /// <summary>One "option NAME" pair per passed-on variable, each followed by a space.</summary>
    /// <param name="option">"--env" for wasmtime, "-e" for docker.</param>
    /// <returns>The options, or "" when no variable is passed on.</returns>
    private string ForwardArguments(string option) => string.Concat(_forwarded.Select(name => option + " " + Quote(name) + " "));

    /// <summary>
    /// For a program run through WSL: WSLENV for wsl.exe, listing the passed-on names
    /// (<see cref="_forwardedToWsl"/>: dotnet run -e's) with the /u flag (from Windows into WSL),
    /// after the entries WSLENV already has, so a user's own entries keep working. Set through
    /// <see cref="EnvironmentVariablesToSet"/>, which the .NET 11 SDK's CLI applies to wsl.exe;
    /// nothing when no variable is passed on.
    /// </summary>
    /// <remarks>
    /// The current WSLENV is the one dotnet run -e gave (an item named WSLENV), else this
    /// process's: ComputeRunArguments runs inside the CLI, whose environment wsl.exe inherits. A
    /// name WSLENV already lists, with any flags, is not listed twice.
    /// </remarks>
    private void SetWslEnvironment()
    {
        if (_forwardedToWsl.Count == 0)
        {
            return;
        }

        string current = EnvironmentVariables.LastOrDefault(item => item.ItemSpec.Equals("WSLENV", StringComparison.OrdinalIgnoreCase))?.GetMetadata("Value")
            ?? Environment.GetEnvironmentVariable("WSLENV")
            ?? "";
        var entries = current.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var listed = new HashSet<string>(entries.Select(entry => entry.Split('/')[0]), StringComparer.OrdinalIgnoreCase);
        foreach (string name in _forwardedToWsl)
        {
            if (listed.Add(name))
            {
                entries.Add(name + "/u");
            }
        }

        var wslEnv = new TaskItem("WSLENV");
        wslEnv.SetMetadata("Value", string.Join(":", entries));
        EnvironmentVariablesToSet = new ITaskItem[] { wslEnv };
    }

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
