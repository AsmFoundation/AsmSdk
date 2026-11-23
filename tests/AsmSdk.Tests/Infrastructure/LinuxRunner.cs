using Xunit;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>
/// Runs the Linux programs the samples build: natively on Linux, through WSL on Windows, and
/// the .NET sample with dotnet on Linux or in the .NET runtime container elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// Two environment variables choose how, as tests/run.py's --run and --dotnet-run did:
/// <c>ASMSDK_TEST_RUN</c> = auto | native | wsl | none, and <c>ASMSDK_TEST_DOTNET_RUN</c> =
/// auto | native | docker | none. "auto" picks native on Linux, then WSL when it is on PATH (or
/// Docker when it answers with Linux containers), else none. CI sets them per runner: only the Linux job can run the outputs.
/// </para>
/// <para>
/// "none" skips the test with the reason, rather than passing it: a run that could not happen
/// must never look like one that succeeded.
/// </para>
/// </remarks>
internal static class LinuxRunner
{
    /// <summary>How the executables run here: native, wsl or none.</summary>
    /// <exception cref="InvalidOperationException">ASMSDK_TEST_RUN holds an unknown value (thrown by the initializer).</exception>
    public static string ExecutableMode { get; } = Resolve("ASMSDK_TEST_RUN", ["native", "wsl", "none"],
        () => OperatingSystem.IsLinux() ? "native" : OperatingSystem.IsWindows() && Command.OnPath("wsl") ? "wsl" : "none");

    /// <summary>How the .NET sample runs here: native, docker or none.</summary>
    /// <remarks>
    /// "auto" picks docker only when Docker answers now with Linux containers, as the musl and
    /// scenario tests already decide: until 2026-11-23 it picked docker whenever docker was on
    /// PATH, so with Docker Desktop installed but stopped every such test failed ("open
    /// //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified") instead
    /// of being skipped with the reason, and a machine's state read as an SDK failure. An explicit ASMSDK_TEST_DOTNET_RUN=docker still means
    /// docker, and still fails when Docker is not there.
    /// </remarks>
    /// <exception cref="InvalidOperationException">ASMSDK_TEST_DOTNET_RUN holds an unknown value (thrown by the initializer).</exception>
    public static string DotnetMode { get; } = Resolve("ASMSDK_TEST_DOTNET_RUN", ["native", "docker", "none"],
        () => OperatingSystem.IsLinux() ? "native" : DockerRunsLinuxContainers() ? "docker" : "none");

    /// <summary>Runs a built Linux executable with the arguments "a b".</summary>
    /// <param name="executable">The executable's full path, inside the repository.</param>
    /// <param name="libraryPath">LD_LIBRARY_PATH relative to the repository root, or empty.</param>
    /// <returns>The program's exit status and output.</returns>
    /// <remarks>
    /// Skips the calling test when no Linux is available (<see cref="ExecutableMode"/> is none).
    /// The execute bit is set first: NuGet, CI artifacts and Windows file systems drop it.
    /// Through WSL the repository is the working directory (WSL maps it), so the paths stay
    /// relative and need no translation.
    /// </remarks>
    public static CommandResult RunExecutable(string executable, string libraryPath = "")
    {
        if (ExecutableMode == "none")
        {
            Assert.Skip("No Linux to run the executables on (ASMSDK_TEST_RUN=none, or neither Linux nor WSL)");
        }

        if (ExecutableMode == "native")
        {
            // The guard is for the platform analyzer (CA1416): "native" means a Linux host.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            }

            var environment = new Dictionary<string, string?>();
            if (libraryPath.Length != 0)
            {
                environment["LD_LIBRARY_PATH"] = Path.Combine(Repo.Root, libraryPath);
            }

            return Command.Run(executable, ["a", "b"], Repo.Root, environment);
        }

        string relative = Repo.Relative(executable);
        string prefix = libraryPath.Length != 0 ? $"LD_LIBRARY_PATH='{libraryPath}' " : "";
        return Command.Run("wsl", ["-e", "sh", "-c", $"chmod +x '{relative}' && {prefix}'./{relative}' a b"], Repo.Root);
    }

    /// <summary>
    /// Runs a shell command line on Linux, from the repository root: sh natively, or sh inside
    /// WSL on Windows. For the tools that need their own arguments, input files or a redirected
    /// standard input, which <see cref="RunExecutable"/> does not offer.
    /// </summary>
    /// <param name="commandLine">
    /// The command line, for sh. Paths in it must be relative to the repository root and inside
    /// it, so that they mean the same file natively and through WSL's mapping of the drive.
    /// </param>
    /// <returns>The command's exit status and output.</returns>
    /// <remarks>
    /// Skips the calling test when no Linux is available (<see cref="ExecutableMode"/> is none).
    /// Callers prefix "chmod +x" for the executables they start, for the reason given on
    /// <see cref="RunExecutable"/>.
    /// </remarks>
    public static CommandResult RunShell(string commandLine)
    {
        if (ExecutableMode == "none")
        {
            Assert.Skip("No Linux to run the executables on (ASMSDK_TEST_RUN=none, or neither Linux nor WSL)");
        }

        return ExecutableMode == "native"
            ? Command.Run("sh", ["-c", commandLine], Repo.Root)
            : Command.Run("wsl", ["-e", "sh", "-c", commandLine], Repo.Root);
    }

    /// <summary>Runs the .NET sample's Hello.Net.dll.</summary>
    /// <param name="outputFolder">Its build output folder (holds the .dll and the .so files).</param>
    /// <returns>The program's exit status and output.</returns>
    /// <remarks>Skips the calling test when neither Linux nor Docker is available.</remarks>
    public static CommandResult RunDotnetSample(string outputFolder) => RunDotnetProgram(outputFolder, "Hello.Net.dll");

    /// <summary>
    /// Runs a framework-dependent .NET program on Linux: with dotnet on a Linux host, or in the
    /// .NET runtime container elsewhere, with its output folder mounted read-only.
    /// </summary>
    /// <param name="outputFolder">The program's build output folder (the .dll, its dependencies and the .so files).</param>
    /// <param name="assembly">The program's .dll file name in that folder.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <returns>The program's exit status and output.</returns>
    /// <remarks>
    /// Skips the calling test when neither Linux nor Docker is available. An xunit v3 test
    /// project is such a program too: started directly, it runs all its tests and returns
    /// non-zero when any fails.
    /// </remarks>
    public static CommandResult RunDotnetProgram(string outputFolder, string assembly, params string[] arguments)
    {
        if (DotnetMode == "none")
        {
            Assert.Skip($"No Linux, nor a running Docker with Linux containers, to run {assembly} on (ASMSDK_TEST_DOTNET_RUN=none, or auto found neither)");
        }

        return DotnetMode == "native"
            ? Command.Run("dotnet", [Path.Combine(outputFolder, assembly), .. arguments])
            : Command.Run("docker", ["run", "--rm", "-v", $"{outputFolder}:/app:ro", "-w", "/app",
                "mcr.microsoft.com/dotnet/runtime:10.0", "dotnet", assembly, .. arguments]);
    }

    /// <summary>
    /// Whether Docker can run Linux containers on this machine now: docker is on PATH, its
    /// daemon answers, and it is in Linux-container mode (GitHub's Windows runners answer
    /// "windows"). Asked once, by <see cref="DotnetMode"/>'s initializer.
    /// </summary>
    /// <returns>True when "docker info" reports the Linux OS type.</returns>
    private static bool DockerRunsLinuxContainers() =>
        Command.OnPath("docker") && Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root).StdOut.Trim() == "linux";

    /// <summary>Reads a mode variable, or decides "auto".</summary>
    /// <param name="variable">The environment variable.</param>
    /// <param name="allowed">The values it may hold besides "auto".</param>
    /// <param name="auto">What "auto" (or an unset variable) means on this machine.</param>
    /// <returns>The mode.</returns>
    /// <exception cref="InvalidOperationException">The variable holds an unknown value.</exception>
    private static string Resolve(string variable, string[] allowed, Func<string> auto)
    {
        string value = Environment.GetEnvironmentVariable(variable) ?? "auto";
        if (value == "auto")
        {
            return auto();
        }

        return allowed.Contains(value)
            ? value
            : throw new InvalidOperationException($"{variable}={value}: expected auto, {string.Join(", ", allowed)}");
    }
}
