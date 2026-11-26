using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Asm programs as programs: the shared libraries they link reach their output, where
/// BinariesCopyPath puts them, with the run paths that make the OS loader find them there; and
/// dotnet run starts them, on this machine, through WSL or in Docker.
/// </summary>
/// <remarks>
/// <para>
/// The projects: EverywhereProgram links the shared library Everywhere through a
/// ProjectReference (Auto: next to the program), EverywherePackageProgram links it from the
/// package Test.Everywhere with BinariesCopyPath="lib/{rid}" on the PackageReference (Linux and
/// macOS), ProgramLayoutWindows must fail with ASM0047. Every RID is built and inspected on
/// every machine; what this machine can run, runs.
/// </para>
/// <para>
/// The programs exit with 10 * ew_platform() + the number of arguments and print their last
/// argument, so a run proves which RID's build ran, that it found its library (a library left
/// where the loader does not look stops the program before main), and that dotnet run passed the
/// arguments through unchanged.
/// </para>
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class ProgramTests(SdkUnderTest sdk)
{
    /// <summary>The program that references Everywhere.asmproj.</summary>
    private static readonly string Program = Repo.TestProject("EverywhereProgram");

    /// <summary>The program that references the package Test.Everywhere.</summary>
    private static readonly string PackageProgram = Repo.TestProject("EverywherePackageProgram");

    /// <summary>Builds EverywhereProgram once (all eight RIDs), asserted successful.</summary>
    private void BuildProgram()
    {
        CommandResult build = sdk.BuildTestProject("EverywhereProgram");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>Packs Test.Everywhere and builds EverywherePackageProgram once, asserted successful.</summary>
    private void BuildPackageProgram()
    {
        CommandResult pack = NativeLayoutTests.PackEverywhere(sdk);
        Assert.True(pack.ExitCode == 0, pack.Tail());
        CommandResult build = sdk.BuildTestProject("EverywherePackageProgram");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>A program's file name per RID: name.exe on Windows, the bare name elsewhere.</summary>
    /// <param name="rid">The RID.</param>
    /// <param name="name">The AssemblyName.</param>
    /// <returns>The file name.</returns>
    internal static string ProgramFile(string rid, string name) => rid.StartsWith("win-", StringComparison.Ordinal) ? name + ".exe" : name;

    /// <summary>A Release output folder of a test project's RID: bin/Release/libc/&lt;rid&gt;/.</summary>
    /// <param name="project">The test project's name.</param>
    /// <param name="rid">The RID.</param>
    /// <returns>The folder's full path.</returns>
    private static string OutputFolder(string project, string rid) => Repo.TestProjectPath(project, "bin", "Release", "libc", rid);

    /// <summary>
    /// Checks a program's run paths: exactly <paramref name="expected"/> (relative folders, ""
    /// for the program's own) as DT_RUNPATH entries on Linux and LC_RPATH entries on macOS, and
    /// none on Windows, which has no such thing.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="rid">Its RID.</param>
    /// <param name="expected">The folders, relative to the program.</param>
    private void AssertRunPaths(string program, string rid, params string[] expected)
    {
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            Assert.Equal(expected.Select(f => f.Length == 0 ? "@loader_path" : "@loader_path/" + f), MachO.RunPaths(program));
        }
        else if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            string dynamic = sdk.ReadElf(program, "-d");
            string runPath = string.Join(':', expected.Select(f => f.Length == 0 ? "$ORIGIN" : "$ORIGIN/" + f));
            if (expected.Length == 0)
            {
                Assert.DoesNotContain("RUNPATH", dynamic, StringComparison.Ordinal);
                Assert.DoesNotContain("RPATH", dynamic, StringComparison.Ordinal);
            }
            else
            {
                // DT_RUNPATH (ld.lld's default), never the older DT_RPATH, which would also apply
                // to the libraries' own dependencies and outrank LD_LIBRARY_PATH. The column
                // between the tag and its text varies with the table (measured: two and three
                // spaces), so the two are checked apart.
                Assert.Contains("(RUNPATH)", dynamic, StringComparison.Ordinal);
                Assert.Contains($"Library runpath: [{runPath}]", dynamic, StringComparison.Ordinal);
                Assert.DoesNotContain("(RPATH)", dynamic, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// A program gets the shared library it links next to it (BinariesCopyPath Auto is the
    /// program's own folder), and the run path that makes the loader look there: $ORIGIN on
    /// Linux, @loader_path on macOS; Windows looks in the program's folder by itself. On Windows
    /// the program links the import library, and the DLL behind it is what is copied.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void A_program_gets_the_shared_library_it_links_next_to_it(string rid)
    {
        BuildProgram();
        string folder = OutputFolder("EverywhereProgram", rid);
        string program = Path.Combine(folder, ProgramFile(rid, "everywhere-program"));
        Assert.True(File.Exists(program), $"{rid}: no {program}");
        Assert.True(File.Exists(Path.Combine(folder, CrossPlatformTests.LibraryFile(rid, "everywhere"))), $"{rid}: the library is not next to the program: {string.Join(", ", Directory.GetFiles(folder).Select(Path.GetFileName))}");
        AssertRunPaths(program, rid, "");
        if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            Assert.Contains("Shared library: [libeverywhere.so]", sdk.ReadElf(program, "-d"), StringComparison.Ordinal);
        }
        else if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            Assert.Contains("Name: everywhere.dll", sdk.ReadObj(program, "--coff-imports"), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("@rpath/libeverywhere.dylib", sdk.ReadObj(program, "--needed-libs"), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// BinariesCopyPath on a PackageReference places a package's library (lib/{rid} here, which
    /// wins over the project's RidFolders), and the program's run path follows it. On Windows the
    /// DLL comes from the package's runtimes/ folder (AsmRuntimeFile in the package's props),
    /// next to the program.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void BinariesCopyPath_on_a_PackageReference_places_the_library_and_the_run_path(string rid)
    {
        BuildPackageProgram();
        string folder = OutputFolder("EverywherePackageProgram", rid);
        string program = Path.Combine(folder, ProgramFile(rid, "everywhere-package-program"));
        Assert.True(File.Exists(program), $"{rid}: no {program}");
        string library = CrossPlatformTests.LibraryFile(rid, "everywhere");
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            Assert.True(File.Exists(Path.Combine(folder, library)), $"{rid}: no {library} next to the program");
            Assert.False(Directory.Exists(Path.Combine(folder, "lib")), $"{rid}: a lib folder for a Windows program");
            return;
        }

        Assert.True(File.Exists(Path.Combine(folder, "lib", rid, library)), $"{rid}: no lib/{rid}/{library}");
        Assert.False(File.Exists(Path.Combine(folder, library)), $"{rid}: {library} next to the program as well");
        Assert.False(Directory.Exists(Path.Combine(folder, rid)), $"{rid}: the project's RidFolders was used, not the reference's lib/{{rid}}");
        AssertRunPaths(program, rid, $"lib/{rid}");
    }

    /// <summary>
    /// A program that links no shared library is linked as before: no run path, and nothing but
    /// the program in its folder (EverywhereLibc links only the C library).
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-musl-arm64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    public void A_program_that_links_no_shared_library_gets_no_run_path(string rid)
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibc");
        Assert.True(build.ExitCode == 0, build.Tail());
        string folder = OutputFolder("EverywhereLibc", rid);
        string program = ProgramFile(rid, "everywhere-libc");
        Assert.Equal([program], Directory.GetFiles(folder).Select(Path.GetFileName));
        AssertRunPaths(Path.Combine(folder, program), rid);
    }

    /// <summary>
    /// A Windows program cannot keep its DLLs in a subfolder: Windows has no run path, and the
    /// program would not start. ASM0047 names the library and AppFolder.
    /// </summary>
    [Fact]
    public void A_windows_program_keeps_its_DLLs_next_to_it()
    {
        CommandResult build = sdk.BuildTestProject("ProgramLayoutWindows");
        Assert.True(build.ExitCode != 0, $"ProgramLayoutWindows built, but must fail: {build.Tail()}");
        Assert.Contains("ASM0047", build.StdOut, StringComparison.Ordinal);
        Assert.Contains("everywhere.dll would be copied to native/", build.StdOut, StringComparison.Ordinal);
        Assert.Contains("AppFolder", build.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The build of this machine's RID, or a skip when AsmSdk builds none for it.</summary>
    /// <returns>The RID.</returns>
    /// <exception cref="Exception">Xunit's skip exception, when there is nothing to run here.</exception>
    private static string HostRidOrSkip()
    {
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no program for this machine ({Repo.HostRid})");
        }

        return rid;
    }

    /// <summary>Checks a dotnet run of the programs: its exit status names the RID that ran and counts the arguments, and the last argument came through.</summary>
    /// <param name="run">The dotnet run result.</param>
    /// <param name="rid">The RID that must have run.</param>
    /// <param name="arguments">The arguments given after "--".</param>
    private static void AssertRan(CommandResult run, string rid, params string[] arguments)
    {
        int expected = 10 * CrossPlatformTests.PlatformCode[rid] + arguments.Length;
        Assert.True(run.ExitCode == expected, $"exit {run.ExitCode}, want {expected} ({rid} with {arguments.Length} arguments): {run.Tail()}");
        Assert.Contains(arguments[^1], run.StdOut.Split('\n').Select(l => l.TrimEnd('\r')));
    }

    /// <summary>
    /// dotnet run builds every RID the program lists and runs this machine's build, with the
    /// arguments after "--" as they were given (one with a space, one with a quote).
    /// </summary>
    [Fact]
    public void Dotnet_run_runs_this_machines_build()
    {
        string rid = HostRidOrSkip();
        CommandResult run = sdk.DotnetRun(Program, ["-c", "Release"], "two words", "a\"quote");
        AssertRan(run, rid, "two words", "a\"quote");
    }

    /// <summary>
    /// dotnet run -r of this machine's RID runs that build too, and the package program finds its
    /// library in lib/&lt;rid&gt;/ through its run path (on Windows next to it), at run time.
    /// </summary>
    [Fact]
    public void Dotnet_run_with_a_rid_runs_the_package_program()
    {
        string rid = HostRidOrSkip();
        BuildPackageProgram();
        CommandResult run = sdk.DotnetRun(PackageProgram, ["-c", "Release", "-r", rid], "x");
        AssertRan(run, rid, "x");
    }

    /// <summary>
    /// On Windows, a Linux build of this machine's CPU runs through WSL when the program has no
    /// Windows build to run (here: dotnet run -r linux-x64, AsmRunIn=Wsl to be sure WSL is
    /// what ran it), with the arguments unchanged.
    /// </summary>
    [Fact]
    public void Dotnet_run_runs_a_linux_build_through_WSL()
    {
        if (!OperatingSystem.IsWindows() || LinuxRunner.ExecutableMode != "wsl" || Command.Run("wsl", ["--exec", "true"]).ExitCode != 0)
        {
            Assert.Skip("No WSL with a Linux distribution here");
        }

        string rid = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        CommandResult run = sdk.DotnetRun(Program, ["-c", "Release", "-r", rid, "-p:AsmRunIn=Wsl"], "x y", "z");
        AssertRan(run, rid, "x y", "z");
    }

    /// <summary>EchoProgram: prints its arguments, then ASM_PROBE ("(unset)" without it), and exits with their number.</summary>
    private static string EchoProgram => Repo.TestProject("EchoProgram");

    /// <summary>This machine's glibc Linux RID: the build WSL runs, and the one Docker runs without emulation.</summary>
    private static string LinuxRidOfThisCpu =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    /// <summary>Skips the calling test unless this is Windows with WSL and a Linux distribution.</summary>
    /// <exception cref="Exception">Xunit's skip exception.</exception>
    private static void RequireWsl()
    {
        if (!OperatingSystem.IsWindows() || LinuxRunner.ExecutableMode != "wsl" || Command.Run("wsl", ["--exec", "true"]).ExitCode != 0)
        {
            Assert.Skip("No WSL with a Linux distribution here");
        }
    }

    /// <summary>Skips the calling test unless Docker runs Linux containers here.</summary>
    /// <exception cref="Exception">Xunit's skip exception.</exception>
    private static void RequireLinuxDocker()
    {
        if (!Command.OnPath("docker") || Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root).StdOut.Trim() != "linux")
        {
            Assert.Skip("No Docker running Linux containers here");
        }
    }

    /// <summary>
    /// dotnet run -e reaches a Linux program run through WSL: WSL passes a Windows variable only
    /// when WSLENV names it, and the SDK sets WSLENV on wsl.exe (as a RuntimeEnvironmentVariable
    /// item, which the .NET 11 SDK's CLI applies after ComputeRunArguments), keeping the entries
    /// WSLENV already has. Skipped on the .NET 10 SDK, whose dotnet run passes -e to no build.
    /// </summary>
    [Fact]
    public void Dotnet_run_passes_e_variables_through_WSL()
    {
        RequireWsl();
        if (sdk.SdkMajorVersion() < 11)
        {
            Assert.Skip($"The .NET {sdk.SdkMajorVersion()} SDK's dotnet run passes -e to no build, so the SDK cannot list the variables in WSLENV (it can from the .NET 11 SDK on)");
        }

        CommandResult run = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Wsl", "--no-launch-profile", "-e", "ASM_PROBE=through-wsl"], "x");
        Assert.True(run.ExitCode == 1, $"exit {run.ExitCode}, want 1 (one argument): {run.Tail()}");
        Assert.Contains("through-wsl", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The lines a run printed, without line ends.</summary>
    /// <param name="run">The dotnet run result.</param>
    /// <returns>The lines of standard output.</returns>
    private static string[] Lines(CommandResult run) => run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    /// <summary>
    /// A launch profile's arguments and variable reach a program run through WSL, through the
    /// launcher the SDK puts between dotnet run and wsl.exe (the CLI applies a profile's
    /// arguments only to a command without arguments of its own; WSL passes a Windows variable
    /// only when WSLENV names it, and the launcher names those that are set when it starts).
    /// A variable another profile sets is not set by the one that runs (Bare), and must stay
    /// absent, never empty: EchoProgram prints "(unset)" only for an absent variable, and WSL
    /// would pass a listed name the environment lacks as an EMPTY one (measured). Any SDK.
    /// </summary>
    [Fact]
    public void A_launch_profiles_arguments_and_variable_reach_WSL_and_another_profiles_stays_unset()
    {
        RequireWsl();
        CommandResult run = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Wsl"]);
        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}, want 2 (the profile's two arguments): {run.Tail()}");
        Assert.Contains("from-profile", Lines(run));
        Assert.Contains("two words", Lines(run));
        Assert.Contains("profile-value", Lines(run));

        CommandResult bare = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Wsl", "--launch-profile", "Bare"]);
        Assert.True(bare.ExitCode == 1, $"exit {bare.ExitCode}, want 1 (the profile's one argument): {bare.Tail()}");
        Assert.Contains("bare-profile", Lines(bare));
        Assert.Contains("(unset)", Lines(bare));
    }

    /// <summary>
    /// Without the launch profile a variable stays out of WSL even though a profile names it:
    /// dotnet run --no-launch-profile sets none, and the launcher lists only variables that are set.
    /// </summary>
    [Fact]
    public void Without_the_launch_profile_its_variable_stays_unset_in_WSL()
    {
        RequireWsl();
        CommandResult run = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Wsl", "--no-launch-profile"], "y");
        Assert.True(run.ExitCode == 1, $"exit {run.ExitCode}, want 1 (one argument): {run.Tail()}");
        Assert.Contains("(unset)", Lines(run));
    }

    /// <summary>
    /// A launch profile's arguments reach a Linux program in a Docker container, through the
    /// launcher (docker run always has arguments of its own, so the CLI would apply none), with
    /// the profile's variable (docker run -e NAME). Any SDK.
    /// </summary>
    [Fact]
    public void A_launch_profiles_arguments_reach_a_program_in_docker()
    {
        RequireLinuxDocker();
        CommandResult run = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Docker"]);
        Assert.True(run.ExitCode == 2, $"exit {run.ExitCode}, want 2 (the profile's two arguments): {run.Tail()}");
        Assert.Contains("from-profile", Lines(run));
        Assert.Contains("two words", Lines(run));
        Assert.Contains("profile-value", Lines(run));
    }

    /// <summary>
    /// A launch profile's variables reach a Linux program in a Docker container: the SDK names
    /// every profile's variables to docker run (-e NAME, no value), which takes their values from
    /// its own environment, where the CLI put the running profile's (an unset name passes
    /// nothing). Any SDK: the names come from Properties/launchSettings.json.
    /// </summary>
    [Fact]
    public void Dotnet_run_passes_a_launch_profiles_variables_into_docker()
    {
        RequireLinuxDocker();
        CommandResult run = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Docker"], "z");
        Assert.True(run.ExitCode == 1, $"exit {run.ExitCode}, want 1 (one argument): {run.Tail()}");
        Assert.Contains("profile-value", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// dotnet run -e reaches a Linux program in a Docker container (docker run -e NAME, its value
    /// from docker's environment). Skipped on the .NET 10 SDK, whose dotnet run passes -e to no
    /// build: there the SDK cannot know the names.
    /// </summary>
    [Fact]
    public void Dotnet_run_passes_e_variables_into_docker()
    {
        RequireLinuxDocker();
        if (sdk.SdkMajorVersion() < 11)
        {
            Assert.Skip($"The .NET {sdk.SdkMajorVersion()} SDK's dotnet run passes -e to no build, so the SDK cannot name the variables to docker (it can from the .NET 11 SDK on)");
        }

        CommandResult run = sdk.DotnetRun(EchoProgram, ["-c", "Release", "-r", LinuxRidOfThisCpu, "-p:AsmRunIn=Docker", "--no-launch-profile", "-e", "ASM_PROBE=from-e"], "w");
        Assert.True(run.ExitCode == 1, $"exit {run.ExitCode}, want 1 (one argument): {run.Tail()}");
        Assert.Contains("from-e", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>The Linux builds Docker runs, with Docker's platform for each CPU.</summary>
    public static readonly TheoryData<string, string> DockerRids = new()
    {
        { "linux-musl-x64", "linux/amd64" },
        { "linux-arm64", "linux/arm64" },
        { "linux-musl-arm64", "linux/arm64" },
    };

    /// <summary>
    /// A Linux build runs in a Docker container (alpine for musl, debian for glibc), another CPU's
    /// under the emulation Docker Desktop includes, with the program's folder (and so its
    /// library) mounted.
    /// </summary>
    /// <param name="rid">The RID to run.</param>
    /// <param name="platform">Docker's platform for its CPU, for the skip probe.</param>
    [Theory]
    [MemberData(nameof(DockerRids))]
    public void Dotnet_run_runs_a_linux_build_in_docker(string rid, string platform)
    {
        if (!Command.OnPath("docker") || Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root).StdOut.Trim() != "linux")
        {
            Assert.Skip("No Docker running Linux containers here");
        }

        if (Command.Run("docker", ["run", "--rm", "--platform", platform, "alpine:3", "true"], Repo.Root).ExitCode != 0)
        {
            Assert.Skip($"Docker here runs no {platform} containers (no emulation for that CPU)");
        }

        CommandResult run = sdk.DotnetRun(Program, ["-c", "Release", "-r", rid, "-p:AsmRunIn=Docker"], "in a container");
        AssertRan(run, rid, "in a container");
    }

    /// <summary>dotnet run of a library stops with ASM0090, which says that dotnet run starts programs.</summary>
    [Fact]
    public void Dotnet_run_of_a_library_says_it_runs_programs()
    {
        CommandResult run = sdk.DotnetRun(Repo.TestProject("Everywhere"), ["-c", "Release"]);
        Assert.True(run.ExitCode != 0, run.Tail());
        Assert.Contains("ASM0090", run.Output, StringComparison.Ordinal);
        Assert.Contains("<OutputType>Exe</OutputType>", run.Output, StringComparison.Ordinal);
    }

    /// <summary>An AsmRunIn that is not Auto, Host, Wsl or Docker stops with ASM0093.</summary>
    [Fact]
    public void Dotnet_run_refuses_an_unknown_AsmRunIn()
    {
        BuildProgram();
        CommandResult run = sdk.DotnetRun(Program, ["-c", "Release", "-p:AsmRunIn=Elsewhere"]);
        Assert.True(run.ExitCode != 0, run.Tail());
        Assert.Contains("ASM0093", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build this machine cannot run itself, with AsmRunIn=Host, stops with ASM0091, which
    /// names this machine and what was tried.
    /// </summary>
    [Fact]
    public void Dotnet_run_says_when_nothing_here_runs_the_build()
    {
        string rid = OperatingSystem.IsWindows() ? "osx-arm64" : "win-x64";
        CommandResult run = sdk.DotnetRun(Program, ["-c", "Release", "-r", rid, "-p:AsmRunIn=Host"]);
        Assert.True(run.ExitCode != 0, run.Tail());
        Assert.Contains("ASM0091", run.Output, StringComparison.Ordinal);
        Assert.Contains($"cannot run any build of EverywhereProgram ({rid})", run.Output, StringComparison.Ordinal);
    }

    /// <summary>The files a publish of EverywhereProgram holds for one RID: the program, the library next to it, the data file.</summary>
    /// <param name="rid">The RID.</param>
    /// <returns>The relative paths, with forward slashes, sorted.</returns>
    private static IEnumerable<string> PublishedProgramFiles(string rid) =>
        new[] { ProgramFile(rid, "everywhere-program"), CrossPlatformTests.LibraryFile(rid, "everywhere"), "data/settings.txt" }.Order(StringComparer.Ordinal);

    /// <summary>Every file under a folder, relative to it, with forward slashes, sorted.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The relative paths; empty when the folder does not exist.</returns>
    private static IEnumerable<string> FilesUnder(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    /// The build output already has what the program needs, data files included: a None item
    /// marked CopyToOutputDirectory is copied next to the program in every RID's folder.
    /// </summary>
    [Fact]
    public void The_build_output_holds_the_programs_data_files()
    {
        BuildProgram();
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.True(File.Exists(Path.Combine(OutputFolder("EverywhereProgram", rid), "data", "settings.txt")), $"{rid}: no data/settings.txt in the build output");
        }
    }

    /// <summary>
    /// dotnet publish -r publishes one RID into .NET's folder, bin/Release/libc/&lt;rid&gt;/publish/,
    /// in Release without -c (PublishRelease): the program, its library, its data file, and
    /// nothing a managed publish would add (no obj/ assembly, no deps.json). The published program
    /// runs where it is, when it is this machine's.
    /// </summary>
    [Fact]
    public void Dotnet_publish_with_a_rid_publishes_that_rid()
    {
        string rid = HostRidOrSkip();
        CommandResult publish = sdk.Once($"publish EverywhereProgram -r {rid}", () => sdk.Dotnet("publish", Program, "-r", rid));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        string folder = Path.Combine(OutputFolder("EverywhereProgram", rid), "publish");
        Assert.Equal(PublishedProgramFiles(rid), FilesUnder(folder));
        CommandResult run = Command.Run(Path.Combine(folder, ProgramFile(rid, "everywhere-program")), ["published"]);
        Assert.True(run.ExitCode == 10 * CrossPlatformTests.PlatformCode[rid] + 1, $"exit {run.ExitCode}: {run.Output}");
    }

    /// <summary>
    /// dotnet publish without -r publishes every RID the program lists, each into a folder of
    /// its own: bin/Release/libc/publish/&lt;rid&gt;/.
    /// </summary>
    [Fact]
    public void Dotnet_publish_without_a_rid_publishes_every_rid_into_its_own_folder()
    {
        CommandResult publish = sdk.Once("publish EverywhereProgram", () => sdk.Dotnet("publish", Program));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        string root = Repo.TestProjectPath("EverywhereProgram", "bin", "Release", "libc", "publish");
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Order(StringComparer.Ordinal), Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.Equal(PublishedProgramFiles(rid), FilesUnder(Path.Combine(root, rid)));
        }
    }

    /// <summary>
    /// dotnet publish -o without -r puts each RID in &lt;folder&gt;/&lt;rid&gt;/, and only the RIDs a
    /// referencing project asks for when one does (AsmRequestedRuntimeIdentifiers, what a
    /// ProjectReference's RuntimeIdentifiers metadata becomes).
    /// </summary>
    [Fact]
    public void Dotnet_publish_to_a_folder_publishes_the_requested_rids()
    {
        string output = Path.Combine(Repo.Artifacts, "test-run", "program-publish-o");
        // %2C: MSBuild's -p splits on a bare comma, as on a semicolon.
        CommandResult publish = sdk.Once("publish EverywhereProgram -o requested", () => sdk.Dotnet("publish", Program, "-o", output, "-p:AsmRequestedRuntimeIdentifiers=linux-x64%2Cwin-arm64"));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.Equal(["linux-x64", "win-arm64"], Directory.GetDirectories(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(PublishedProgramFiles("linux-x64"), FilesUnder(Path.Combine(output, "linux-x64")));
        Assert.Equal(PublishedProgramFiles("win-arm64"), FilesUnder(Path.Combine(output, "win-arm64")));
    }

    /// <summary>
    /// dotnet publish -r -o publishes that RID into the folder itself, as .NET does; a package's
    /// library goes where BinariesCopyPath put it in the build (lib/&lt;rid&gt;/, the run path's
    /// folder), and not also flat, where the managed SDK would publish a package's runtimes/ file.
    /// </summary>
    [Fact]
    public void Dotnet_publish_places_a_packages_library_by_BinariesCopyPath()
    {
        BuildPackageProgram();
        string output = Path.Combine(Repo.Artifacts, "test-run", "package-program-publish");
        CommandResult publish = sdk.Once("publish EverywherePackageProgram -r linux-x64 -o", () => sdk.Dotnet("publish", PackageProgram, "-r", "linux-x64", "-o", output));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.Equal(["everywhere-package-program", "lib/linux-x64/libeverywhere.so"], FilesUnder(output));
        AssertRunPaths(Path.Combine(output, "everywhere-package-program"), "linux-x64", "lib/linux-x64");
    }

    /// <summary>The .NET program that references EverywhereProgram.</summary>
    private static readonly string NetProgram = Repo.TestProjectPath("NetProgram", "NetProgram.csproj");

    /// <summary>The files EverywhereProgram brings to a consumer for one RID: the program and the library next to it.</summary>
    /// <param name="rid">The RID.</param>
    /// <returns>The file names, sorted.</returns>
    private static IEnumerable<string> ProgramBundle(string rid) =>
        new[] { ProgramFile(rid, "everywhere-program"), CrossPlatformTests.LibraryFile(rid, "everywhere") }.Order(StringComparer.Ordinal);

    /// <summary>
    /// Checks a run of NetProgram or NetProgramPackage: it found everywhere-program, started this
    /// machine's build of it with one argument, and returned its exit status.
    /// </summary>
    /// <param name="run">The run of the .NET program.</param>
    /// <param name="rid">This machine's RID.</param>
    private static void AssertStartedProgram(CommandResult run, string rid)
    {
        int expected = 10 * CrossPlatformTests.PlatformCode[rid] + 1;
        Assert.True(run.ExitCode == expected, $"exit {run.ExitCode}, want {expected}: {run.Output}");
        Assert.Contains("from .NET", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// A .NET app that references an asm program gets it, in a portable build, for every RID the
    /// program lists, each with the library it links next to it under runtimes/&lt;rid&gt;/native/
    /// (Auto), listed in deps.json; the app finds this machine's through the host's native search
    /// folders and starts it. The program's own reference to the library does not reach the app
    /// (a program's references are private): the library is there once, in the program's bundle.
    /// </summary>
    [Fact]
    public void A_dotnet_app_gets_a_referenced_program_for_every_rid()
    {
        string rid = HostRidOrSkip();
        string output = Path.Combine(Repo.Artifacts, "test-run", "net-program-portable");
        CommandResult build = sdk.Once("build NetProgram portable", () => sdk.Dotnet("build", NetProgram, "-c", "Release", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        foreach (string each in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.Equal(ProgramBundle(each), FilesUnder(Path.Combine(output, "runtimes", each, "native")).Where(f => !f.EndsWith(".pdb", StringComparison.Ordinal)));
        }

        string deps = File.ReadAllText(Path.Combine(output, "NetProgram.deps.json"));
        Assert.Contains($"runtimes/{rid}/native/{ProgramFile(rid, "everywhere-program")}", deps, StringComparison.Ordinal);
        Assert.DoesNotContain("Test.Everywhere/", deps, StringComparison.Ordinal);
        AssertStartedProgram(Command.Run("dotnet", [Path.Combine(output, "NetProgram.dll")], Repo.Root), rid);
    }

    /// <summary>A RID-specific .NET app gets that RID's program and library next to it, and starts it.</summary>
    [Fact]
    public void A_rid_specific_dotnet_app_gets_its_rids_program_next_to_it()
    {
        string rid = HostRidOrSkip();
        string output = Path.Combine(Repo.Artifacts, "test-run", "net-program-rid");
        CommandResult build = sdk.Once($"build NetProgram -r {rid}", () => sdk.Dotnet("build", NetProgram, "-c", "Release", "-r", rid, "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        foreach (string file in ProgramBundle(rid))
        {
            Assert.True(File.Exists(Path.Combine(output, file)), $"no {file} next to the app");
        }

        Assert.False(Directory.Exists(Path.Combine(output, "runtimes")), "a runtimes folder in a RID-specific build");
        AssertStartedProgram(Command.Run("dotnet", [Path.Combine(output, "NetProgram.dll")], Repo.Root), rid);
    }

    /// <summary>
    /// BinariesCopyPath on the reference places a program with what it needs (tools/{rid}), and the
    /// app still finds it through deps.json.
    /// </summary>
    [Fact]
    public void BinariesCopyPath_places_a_referenced_program_with_its_library()
    {
        string rid = HostRidOrSkip();
        string output = Path.Combine(Repo.Artifacts, "test-run", "net-program-tools");
        CommandResult build = sdk.Once("build NetProgram tools/{rid}", () => sdk.Dotnet("build", NetProgram, "-c", "Release", "-p:NetProgramFolder=tools/{rid}", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        foreach (string each in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.Equal(ProgramBundle(each), FilesUnder(Path.Combine(output, "tools", each)).Where(f => !f.EndsWith(".pdb", StringComparison.Ordinal)));
        }

        Assert.False(Directory.Exists(Path.Combine(output, "runtimes")), "files in runtimes/ as well");
        AssertStartedProgram(Command.Run("dotnet", [Path.Combine(output, "NetProgram.dll")], Repo.Root), rid);
    }

    /// <summary>
    /// An asm program delivers the asm programs it references, with what they need, where its
    /// BinariesCopyPath says (helpers/, on Windows too: a program is started, not loaded), and the
    /// library it links next to itself.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void An_asm_program_delivers_the_programs_it_references(string rid)
    {
        CommandResult build = sdk.BuildTestProject("ProgramHost");
        Assert.True(build.ExitCode == 0, build.Tail());
        string folder = OutputFolder("ProgramHost", rid);
        string library = CrossPlatformTests.LibraryFile(rid, "everywhere");
        Assert.Equal(
            new[] { ProgramFile(rid, "program-host"), library }.Concat(ProgramBundle(rid).Select(f => "helpers/" + f)).Order(StringComparer.Ordinal),
            FilesUnder(folder));
    }

    /// <summary>
    /// dotnet pack of an asm program: runtimes/&lt;rid&gt;/native/ holds the program and the library
    /// next to it for every RID (the extensionless Linux and macOS programs as files, not folders),
    /// the props deliver it to an asm consumer (AsmReferencedProgram), the targets give a .NET
    /// consumer BinariesCopyPath, and the package depends on nothing.
    /// </summary>
    [Fact]
    public void A_program_package_holds_the_program_and_its_library()
    {
        CommandResult pack = PackProgram(sdk);
        Assert.True(pack.ExitCode == 0, pack.Tail());
        string nupkg = Path.Combine(Repo.Artifacts, "packages", "Test.EverywhereProgram.1.0.0.nupkg");
        SortedSet<string> entries = SdkUnderTest.PackageEntries(nupkg);
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            foreach (string file in ProgramBundle(rid))
            {
                Assert.Contains($"runtimes/{rid}/native/{file}", entries);
            }
        }

        Assert.Contains("buildTransitive/netstandard2.0/Test.EverywhereProgram.targets", entries);
        using var zip = System.IO.Compression.ZipFile.OpenRead(nupkg);
        string props = new StreamReader(zip.GetEntry("build/netstandard2.0/Test.EverywhereProgram.props")!.Open()).ReadToEnd();
        Assert.Contains("<AsmReferencedProgram Include=\"$(MSBuildThisFileDirectory)../../runtimes/linux-x64/native/everywhere-program\"", props, StringComparison.Ordinal);
        string nuspec = new StreamReader(zip.GetEntry("Test.EverywhereProgram.nuspec")!.Open()).ReadToEnd();
        Assert.DoesNotContain("<dependency ", nuspec, StringComparison.Ordinal);
    }

    /// <summary>Packs EverywhereProgram as Test.EverywhereProgram 1.0.0 into the local feed, once.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The pack's result.</returns>
    internal static CommandResult PackProgram(SdkUnderTest sdk) =>
        sdk.Once("pack EverywhereProgram", () => sdk.Dotnet("pack", Program, "-c", "Release"));

    /// <summary>A .NET app gets the program from its package (NuGet's runtimes/ rules) and starts it.</summary>
    [Fact]
    public void A_dotnet_app_runs_a_program_from_its_package()
    {
        string rid = HostRidOrSkip();
        CommandResult pack = PackProgram(sdk);
        Assert.True(pack.ExitCode == 0, pack.Tail());
        string output = Path.Combine(Repo.Artifacts, "test-run", "net-program-package");
        CommandResult build = sdk.Once("build NetProgramPackage", () => sdk.Dotnet("build", Repo.TestProjectPath("NetProgramPackage", "NetProgramPackage.csproj"), "-c", "Release", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("asmfoundation.sdk", File.ReadAllText(Repo.TestProjectPath("NetProgramPackage", "obj", "project.assets.json")), StringComparison.OrdinalIgnoreCase);
        AssertStartedProgram(Command.Run("dotnet", [Path.Combine(output, "NetProgramPackage.dll")], Repo.Root), rid);
    }

    /// <summary>
    /// A .NET library's package carries a program it references with PrivateAssets="all": every
    /// RID's program and library, flat in runtimes/&lt;rid&gt;/native/, and the targets.
    /// </summary>
    [Fact]
    public void A_dotnet_library_package_carries_a_private_program_reference()
    {
        CommandResult pack = sdk.Once("pack NetProgramLibrary", () => sdk.Dotnet("pack", Repo.TestProjectPath("NetProgramLibrary", "NetProgramLibrary.csproj"), "-c", "Release"));
        Assert.True(pack.ExitCode == 0, pack.Tail());
        SortedSet<string> entries = SdkUnderTest.PackageEntries(Path.Combine(Repo.Artifacts, "packages", "Test.NetProgramLibrary.1.0.0.nupkg"));
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            foreach (string file in ProgramBundle(rid))
            {
                Assert.Contains($"runtimes/{rid}/native/{file}", entries);
            }
        }

        Assert.Contains("buildTransitive/net10.0/Test.NetProgramLibrary.targets", entries);
    }

    /// <summary>
    /// A program whose libraries sit in a subfolder cannot be packed: NuGet copies runtimes/ files
    /// flat into a consumer built for one RID, where the program's run path would find nothing.
    /// ASM0051 says so (EverywherePackageProgram keeps its library in lib/{rid} on Linux and macOS).
    /// </summary>
    [Fact]
    public void A_program_with_libraries_in_a_subfolder_cannot_be_packed()
    {
        BuildPackageProgram();
        CommandResult pack = sdk.Once("pack EverywherePackageProgram", () => sdk.Dotnet("pack", PackageProgram, "-c", "Release"));
        Assert.True(pack.ExitCode != 0, $"EverywherePackageProgram packed, but must fail: {pack.Tail()}");
        Assert.Contains("ASM0051", pack.StdOut, StringComparison.Ordinal);
        Assert.Contains("lib/linux-x64/libeverywhere.so", pack.StdOut, StringComparison.Ordinal);
    }

    /// <summary>dotnet run --no-build of a program that was never built (here: into an empty output folder) stops with ASM0092.</summary>
    [Fact]
    public void Dotnet_run_without_a_build_says_so()
    {
        BuildProgram();
        string output = Path.Combine(Repo.Artifacts, "test-run", "program-no-build") + Path.DirectorySeparatorChar;
        CommandResult run = sdk.DotnetRun(Program, ["-c", "Release", "--no-build", $"-p:BaseOutputPath={output}"]);
        Assert.True(run.ExitCode != 0, run.Tail());
        Assert.Contains("ASM0092", run.Output, StringComparison.Ordinal);
    }
}
