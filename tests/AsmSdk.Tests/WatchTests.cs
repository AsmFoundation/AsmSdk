using System.Diagnostics;
using System.Text;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// dotnet watch with an asm program: it builds and runs the program, and runs it again when a file
/// the program is built from changes. .NET's dotnet watch watches each project's Compile,
/// AdditionalFiles and Watch items; an asm project's sources are Compile items, and its include
/// files None items, which the SDK lists as Watch items too (0.4.0 did not: editing an .inc
/// changed nothing until a .asm file was saved).
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture): its environment for the watch.</param>
public sealed class WatchTests(SdkUnderTest sdk)
{
    /// <summary>
    /// Editing the include file that holds WatchProgram's message makes dotnet watch build and run
    /// the program again, which then prints the new message. Run on a copy, which the test edits:
    /// artifacts/watch-projects/WatchProgram, beside a copy of tests/projects' global.json,
    /// nuget.config and Directory.Build.props, at the same depth, so the relative feed path of
    /// nuget.config names the same local feed.
    /// </summary>
    /// <remarks>
    /// Both include files change, message.inc (NASM, the x64 build) and message-gnu.inc (the
    /// ARM64 build): this machine's build reads one of them. The polling file watcher
    /// (DOTNET_USE_POLLING_FILE_WATCHER) is asked for, so a CI machine's file system notifications
    /// cannot make the test flaky; it notices a change within a second or two.
    /// </remarks>
    [Fact]
    public void Dotnet_watch_runs_the_program_again_when_an_include_file_changes()
    {
        string root = Path.Combine(Repo.Artifacts, "watch-projects");
        string project = Path.Combine(root, "WatchProgram");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        foreach (string file in new[] { "global.json", "nuget.config", "Directory.Build.props" })
        {
            File.Copy(Repo.TestProjectPath(file), Path.Combine(root, file));
        }

        CopyProject(Repo.TestProjectPath("WatchProgram"), project);

        var environment = new Dictionary<string, string?>(sdk.TestEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_USE_POLLING_FILE_WATCHER"] = "1",
            ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1",
        };
        using var watch = WatchedProcess.Start("dotnet", ["watch", "run", "--project", project, "--non-interactive", "--no-launch-profile"], root, environment);
        watch.WaitFor("watch-program: first", TimeSpan.FromMinutes(5));

        foreach (string include in new[] { "message.inc", "message-gnu.inc" })
        {
            string path = Path.Combine(project, include);
            File.WriteAllText(path, File.ReadAllText(path).Replace("first", "second", StringComparison.Ordinal));
        }

        watch.WaitFor("watch-program: second", TimeSpan.FromMinutes(3));
    }

    /// <summary>Copies a project folder without its build outputs, which the copy makes itself.</summary>
    /// <param name="source">The project folder.</param>
    /// <param name="target">The new folder (created).</param>
    /// <exception cref="IOException">A file cannot be copied.</exception>
    private static void CopyProject(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (string folder in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(folder);
            if (name is not ("bin" or "obj"))
            {
                CopyProject(folder, Path.Combine(target, name));
            }
        }
    }

    /// <summary>
    /// A command that keeps running, such as dotnet watch: its output is gathered as it comes, a
    /// test waits for a text to appear, and disposing it stops the command with everything it
    /// started (the builds, the program).
    /// </summary>
    private sealed class WatchedProcess : IDisposable
    {
        /// <summary>The running command.</summary>
        private readonly Process _process;

        /// <summary>Everything it wrote so far, both streams in arrival order; locked while written or read.</summary>
        private readonly StringBuilder _output = new();

        /// <summary>Creates the wrapper around a started process.</summary>
        /// <param name="process">The process, with both output streams redirected.</param>
        private WatchedProcess(Process process) => _process = process;

        /// <summary>Starts a command with its output redirected and read as it comes.</summary>
        /// <param name="fileName">The program (dotnet, found on PATH).</param>
        /// <param name="arguments">Its arguments, each passed as one.</param>
        /// <param name="workingDirectory">Its working directory.</param>
        /// <param name="environment">Variables to set; a null value removes the variable.</param>
        /// <returns>The running command.</returns>
        /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
        public static WatchedProcess Start(string fileName, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
        {
            var start = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // A stdin of its own, left open: an inherited console stdin could be read by the
                // command, and a closed one could look like the end of input to it.
                RedirectStandardInput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

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

            var process = new Process { StartInfo = start };
            var watched = new WatchedProcess(process);
            // Both streams are read as events: a stream nobody reads fills its pipe and blocks
            // the command, which then never prints what the test waits for.
            process.OutputDataReceived += (_, line) => watched.Append(line.Data);
            process.ErrorDataReceived += (_, line) => watched.Append(line.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return watched;
        }

        /// <summary>Waits until the command's output holds a text, or fails the test with its output.</summary>
        /// <param name="text">The text to wait for.</param>
        /// <param name="timeout">How long to wait.</param>
        /// <exception cref="Exception">
        /// Xunit's assertion exception when the text did not appear in time, or the command ended
        /// before it did; the message holds the output's tail.
        /// </exception>
        public void WaitFor(string text, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                string output = Output;
                if (output.Contains(text, StringComparison.Ordinal))
                {
                    return;
                }

                if (_process.HasExited)
                {
                    Assert.Fail($"The command ended (exit {_process.ExitCode}) before printing \"{text}\":{Environment.NewLine}{Tail(output)}");
                }

                Thread.Sleep(250);
            }

            Assert.Fail($"\"{text}\" did not appear within {timeout.TotalSeconds:F0} s:{Environment.NewLine}{Tail(Output)}");
        }

        /// <summary>What the command has written so far.</summary>
        private string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        /// <summary>Stops the command and every process it started, then releases it.</summary>
        /// <remarks>
        /// The whole tree: dotnet watch starts the program it runs (and may leave a build running),
        /// and those would keep the copy's files open. A command that already ended has nothing
        /// left to stop.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(30_000);
                }
            }
            catch (InvalidOperationException)
            {
                // It ended between the check and the kill: nothing left to stop.
            }

            _process.Dispose();
        }

        /// <summary>Adds a line the command wrote; the null that marks a stream's end adds nothing.</summary>
        /// <param name="line">The line, without its line break.</param>
        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_output)
            {
                _output.AppendLine(line);
            }
        }

        /// <summary>The last characters of an output, enough to see why a wait failed.</summary>
        /// <param name="output">The output.</param>
        /// <returns>Its last 4000 characters, or all of it.</returns>
        private static string Tail(string output) => output.Length <= 4000 ? output : output[^4000..];
    }
}
