using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The programs run: the executables natively on Linux (through WSL on Windows), and the .NET
/// sample that P/Invokes the shared libraries (dotnet on Linux, the .NET runtime container
/// elsewhere). Where neither is available the tests are skipped, with the reason.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class RunTests(SdkUnderTest sdk)
{
    /// <summary>Runs a program and checks its exit status and output.</summary>
    /// <param name="executable">The program.</param>
    /// <param name="status">The exit status it must return.</param>
    /// <param name="expected">Text its output must contain (empty for none).</param>
    /// <param name="libraryPath">LD_LIBRARY_PATH relative to the repository root, or empty.</param>
    private static void Expect(string executable, int status, string expected, string libraryPath = "")
    {
        CommandResult run = LinuxRunner.RunExecutable(executable, libraryPath);
        Assert.True(run.ExitCode == status && run.Output.Contains(expected, StringComparison.Ordinal),
            $"{Repo.Relative(executable)}: exit {run.ExitCode} (want {status}), output: {run.Output}");
    }

    /// <summary>The freestanding executable prints the product it computes with the static library.</summary>
    [Fact]
    public void The_freestanding_executable_runs()
    {
        Assert.True(sdk.SamplesBuild.ExitCode == 0, sdk.SamplesBuild.Tail());
        Expect(Repo.Sample("Hello.App", "bin", "Release", "freestanding", "hello-app"), 0, "6 * 7 = 42");
    }

    /// <summary>The libc executable starts through the SDK's crt1 and sees its three arguments.</summary>
    [Fact]
    public void The_libc_executable_runs()
    {
        Assert.True(sdk.SamplesBuild.ExitCode == 0, sdk.SamplesBuild.Tail());
        Expect(Repo.Sample("Hello.Libc", "bin", "Release", "libc", "hello-libc"), 0, "3 arguments");
    }

    /// <summary>The package consumer runs the code it linked from the Hello.Static package.</summary>
    [Fact]
    public void The_package_consumer_runs()
    {
        CommandResult build = PackageTests.BuildConsumer(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
        Expect(Repo.Sample("consumers", "Hello.Consumer", "bin", "Release", "freestanding", "hello-consumer"), 0, "Hello.Static package");
    }

    /// <summary>The program whose StartupObject is entry_here starts there: it exits 42.</summary>
    [Fact]
    public void The_StartupObject_program_starts_at_its_entry()
    {
        CommandResult build = sdk.BuildTestProject("Entry");
        Assert.True(build.ExitCode == 0, build.Tail());
        Expect(Repo.TestProjectPath("Entry", "bin", "Release", "freestanding", "entry"), 42, "");
    }

    /// <summary>The libc consumer loads the package's shared library from the NuGet cache and exits 11.</summary>
    [Fact]
    public void The_libc_package_consumer_runs()
    {
        CommandResult build = ProjectRuleTests.BuildLibcConsumer(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
        Expect(Repo.TestProjectPath("LibcConsumer", "bin", "Release", "libc", "libc-consumer"), 11, "",
            "artifacts/test-nuget/test.libconly/1.0.0/runtimes/linux-x64/native");
    }

    /// <summary>
    /// The .NET sample P/Invokes both asm libraries, and loaded the libc build of the
    /// multi-targeted libhello.so.
    /// </summary>
    [Fact]
    public void The_dotnet_sample_calls_both_libraries()
    {
        Assert.True(sdk.SamplesBuild.ExitCode == 0, sdk.SamplesBuild.Tail());
        CommandResult run = LinuxRunner.RunDotnetSample(Repo.Sample("Hello.Net", "bin", "Release", "net10.0"));
        Assert.True(run.ExitCode == 0 && !run.StdOut.Contains("FAIL", StringComparison.Ordinal)
                    && run.StdOut.Contains("hello_uses_libc() == 1", StringComparison.Ordinal),
            $"exit {run.ExitCode}: {run.Output}");
    }
}
