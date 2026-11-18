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
/// auto | native | docker | none. "auto" picks native on Linux, then WSL (or Docker) when it is
/// on PATH, else none. CI sets them per runner: only the Linux job can run the outputs.
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
    /// <exception cref="InvalidOperationException">ASMSDK_TEST_DOTNET_RUN holds an unknown value (thrown by the initializer).</exception>
    public static string DotnetMode { get; } = Resolve("ASMSDK_TEST_DOTNET_RUN", ["native", "docker", "none"],
        () => OperatingSystem.IsLinux() ? "native" : Command.OnPath("docker") ? "docker" : "none");

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

    /// <summary>Runs the .NET sample's Hello.Net.dll.</summary>
    /// <param name="outputFolder">Its build output folder (holds the .dll and the .so files).</param>
    /// <returns>The program's exit status and output.</returns>
    /// <remarks>Skips the calling test when neither Linux nor Docker is available.</remarks>
    public static CommandResult RunDotnetSample(string outputFolder)
    {
        if (DotnetMode == "none")
        {
            Assert.Skip("No Linux or Docker to run Hello.Net on (ASMSDK_TEST_DOTNET_RUN=none)");
        }

        return DotnetMode == "native"
            ? Command.Run("dotnet", [Path.Combine(outputFolder, "Hello.Net.dll")])
            : Command.Run("docker", ["run", "--rm", "-v", $"{outputFolder}:/app:ro", "-w", "/app",
                "mcr.microsoft.com/dotnet/runtime:10.0", "dotnet", "Hello.Net.dll"]);
    }

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
