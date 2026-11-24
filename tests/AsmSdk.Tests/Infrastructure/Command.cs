using System.Diagnostics;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>What a finished command printed and returned.</summary>
/// <param name="ExitCode">The exit status (-1 when the command timed out and was killed).</param>
/// <param name="StdOut">Everything it wrote to standard output.</param>
/// <param name="StdErr">Everything it wrote to standard error.</param>
public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Standard output, then standard error: what a failure message should show.</summary>
    public string Output => StdErr.Length == 0 ? StdOut : StdOut + Environment.NewLine + StdErr;

    /// <summary>The last characters of <see cref="Output"/>, enough to show why a build failed.</summary>
    /// <param name="characters">How many characters to keep from the end.</param>
    /// <returns>The tail of the output, whole when it is short.</returns>
    public string Tail(int characters = 3000) =>
        Output.Length <= characters ? Output : "…" + Output[^characters..];
}

/// <summary>Runs external programs (dotnet, the toolchain's llvm-readobj, the built executables, wsl, docker).</summary>
internal static class Command
{
    /// <summary>
    /// How long one command may run. Generous on purpose (a cold samples build on a slow
    /// hosted runner takes minutes); it exists so that a hung tool fails a test instead of
    /// hanging the whole run.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>Runs a program to completion and captures its output.</summary>
    /// <param name="fileName">The program (a path, or a name found on PATH).</param>
    /// <param name="arguments">Its arguments, each passed as one argument (no shell quoting).</param>
    /// <param name="workingDirectory">The working directory; null for the repository root.</param>
    /// <param name="environment">
    /// Variables to set (a null value removes the variable); null to inherit the test
    /// process's environment unchanged.
    /// </param>
    /// <returns>The exit status and the captured output. Never throws for a non-zero exit: tests check.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started (not found).</exception>
    /// <remarks>
    /// Both streams are read asynchronously while the process runs: reading one to the end
    /// before the other deadlocks as soon as the child fills the other pipe's buffer, which a
    /// verbose MSBuild run does. On timeout the whole process tree is killed (dotnet build
    /// leaves MSBuild nodes and tools as children).
    /// </remarks>
    public static CommandResult Run(string fileName, IEnumerable<string> arguments,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Run(start, environment);
    }

    /// <summary>
    /// Runs a program with a raw Windows command line, for the one program that does not read
    /// its arguments the C runtime's way: cmd.exe, whose <c>/s /c "..."</c> line must reach it
    /// exactly as written (<see cref="Run(string, IEnumerable{string}, string?, IReadOnlyDictionary{string, string?}?)"/>
    /// would quote it once more).
    /// </summary>
    /// <param name="fileName">The program (a path, or a name found on PATH).</param>
    /// <param name="commandLine">The arguments as one string, already quoted by the caller.</param>
    /// <param name="workingDirectory">The working directory; null for the repository root.</param>
    /// <param name="environment">Variables to set (a null value removes the variable); null to inherit the test process's environment.</param>
    /// <returns>The exit status and the captured output. Never throws for a non-zero exit: tests check.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started (not found).</exception>
    public static CommandResult RunCommandLine(string fileName, string commandLine,
        string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(fileName, commandLine)
        {
            WorkingDirectory = workingDirectory ?? Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        return Run(start, environment);
    }

    /// <summary>Starts a prepared process with the given environment and waits for it, capturing both streams.</summary>
    /// <param name="start">The process to start: program, arguments, working directory and redirection set.</param>
    /// <param name="environment">Variables to set (a null value removes the variable); null to inherit the test process's environment unchanged.</param>
    /// <returns>The exit status and the captured output; exit status -1 when it timed out and was killed.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started (not found).</exception>
    /// <summary>
    /// Runs a program with bytes on its standard input, as a shell's "&lt; file" gives them, and
    /// captures its output: for a program whose input path is the point of the test (a WASI
    /// command reading fd 0 under wasmtime, where no shell is at hand on every OS).
    /// </summary>
    /// <param name="fileName">The program (a path, or a name found on PATH).</param>
    /// <param name="arguments">Its arguments, each passed as one argument.</param>
    /// <param name="standardInput">Everything the program reads from standard input; the stream is closed after it, so the program sees its end.</param>
    /// <param name="workingDirectory">The working directory; null for the repository root.</param>
    /// <returns>The exit status and the captured output. Never throws for a non-zero exit: tests check.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started (not found).</exception>
    /// <remarks>
    /// The input is written from a task while the outputs are read: written first in full, a
    /// large input would deadlock against a child that fills its output pipe before it reads on.
    /// A child that exits without reading it all closes the pipe, which the writer then ignores.
    /// </remarks>
    public static CommandResult RunWithInput(string fileName, IEnumerable<string> arguments, byte[] standardInput, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? Repo.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Run(start, null, standardInput);
    }

    /// <summary>Starts a process, waits for it (killing its tree on timeout) and captures both outputs.</summary>
    /// <param name="start">How to start it (both outputs redirected; standard input too when there is input to give).</param>
    /// <param name="environment">Variables to set (a null value removes the variable), or null.</param>
    /// <param name="standardInput">Bytes for its standard input, or null when it is not redirected.</param>
    /// <returns>The exit status and the captured output.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
    private static CommandResult Run(ProcessStartInfo start, IReadOnlyDictionary<string, string?>? environment, byte[]? standardInput = null)
    {
        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
            {
                if (value is null)
                {
                    start.Environment.Remove(name);
                }
                else
                {
                    start.Environment[name] = value;
                }
            }
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task? input = null;
        if (standardInput is not null)
        {
            input = Task.Run(() =>
            {
                try
                {
                    process.StandardInput.BaseStream.Write(standardInput, 0, standardInput.Length);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The child exited (or closed its input) before reading everything: what it
                    // did is in its exit status and output, which the test checks.
                }
            });
        }

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return new CommandResult(-1, stdout.Result, stderr.Result + $"{Environment.NewLine}(killed after {Timeout})");
        }

        // WaitForExit(TimeSpan) does not wait for the redirected streams; the reads finish
        // when the child closes its ends, right after it exits. The input writer ends as the
        // child does (its pipe closes), so waiting for it only collects it.
        input?.Wait(TimeSpan.FromSeconds(10));
        return new CommandResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Whether a program can be found on PATH (as `which` or `where` would find it).</summary>
    /// <param name="name">The program's name, without an extension.</param>
    /// <returns>True when an executable of that name is on PATH.</returns>
    public static bool OnPath(string name)
    {
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                if (File.Exists(Path.Combine(dir, name + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
