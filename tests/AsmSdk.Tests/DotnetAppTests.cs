using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// A .NET program that reaches an asm project through a RID-agnostic class library
/// (NetThroughLibrary -> NetLibrary -> Everywhere), the layout of most real solutions, built and
/// published the ways .NET offers: for one RID, into an -o folder, as a single file with the
/// native library beside it or inside it, and with the publish switches on the command line.
/// </summary>
/// <remarks>
/// Each of these failed or shipped dead files before 0.3.0's consumer-target fixes, found by the
/// manual interop QA of 2026-11-20: a RID-specific output carried every RID's runtimes/ copy
/// from the class library; -o made every RID of the portable asm build write into one folder
/// (link errors); a single-file publish lost the native library (Windows) or never bundled it
/// (Linux, macOS); -p:PublishAot=true stopped NuGet's restore in every asm project. What runs,
/// runs on this machine: its own RID's build (skipped on a machine AsmSdk builds no library
/// for). Outputs go to artifacts/test-run/, which each test run starts without.
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class DotnetAppTests(SdkUnderTest sdk)
{
    /// <summary>The NetThroughLibrary project file.</summary>
    private static readonly string App = Repo.TestProjectPath("NetThroughLibrary", "NetThroughLibrary.csproj");

    /// <summary>A folder of this run under artifacts/test-run/.</summary>
    /// <param name="name">The folder's name.</param>
    /// <returns>The full path (not created: dotnet's -o creates it).</returns>
    private static string RunFolder(string name) => Path.Combine(Repo.Artifacts, "test-run", name);

    /// <summary>The app's apphost file name on this machine: NetThroughLibrary.exe or NetThroughLibrary.</summary>
    private static string AppHost => OperatingSystem.IsWindows() ? "NetThroughLibrary.exe" : "NetThroughLibrary";

    /// <summary>The line NetThroughLibrary prints when it loaded the library of <paramref name="rid"/>.</summary>
    /// <param name="rid">The RID the program runs as (this machine's).</param>
    /// <returns>"&lt;rid&gt; 42 &lt;ew_platform()&gt; &lt;ew_os()&gt;", as NetConsumer prints it.</returns>
    private static string Expected(string rid) =>
        $"{rid} 42 {CrossPlatformTests.PlatformCode[rid]} {CrossPlatformTests.OsNameCode[rid]}";

    /// <summary>This machine's RID, or a skip when AsmSdk builds no library for it.</summary>
    /// <returns>The RID.</returns>
    /// <exception cref="Exception">Xunit's skip exception, when there is nothing to run here.</exception>
    private static string HostRidOrSkip()
    {
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        return rid;
    }

    /// <summary>Asserts that a folder holds no runtimes/ folder, listing what it holds if it does.</summary>
    /// <param name="folder">The output or publish folder.</param>
    private static void AssertNoRuntimesFolder(string folder)
    {
        string runtimes = Path.Combine(folder, "runtimes");
        Assert.False(Directory.Exists(runtimes),
            "a RID-specific output carries runtimes/: " + (Directory.Exists(runtimes)
                ? string.Join(", ", Directory.GetFiles(runtimes, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)))
                : ""));
    }

    /// <summary>
    /// dotnet build -r through the class library: the program's folder gets its own RID's
    /// library, flat, and none of the runtimes/&lt;rid&gt;/native/ copies the portable class
    /// library hands on (11 dead files in the QA solution, before 0.3.0); it runs.
    /// </summary>
    [Fact]
    public void A_rid_specific_build_through_a_class_library_copies_only_its_own_rid()
    {
        string rid = HostRidOrSkip();
        CommandResult build = sdk.Once($"build NetThroughLibrary -r {rid}", () => sdk.Dotnet("build", App, "-c", "Release", "-r", rid));
        Assert.True(build.ExitCode == 0, build.Tail());
        string output = Repo.TestProjectPath("NetThroughLibrary", "bin", "Release", "net10.0", rid);
        Assert.True(File.Exists(Path.Combine(output, CrossPlatformTests.LibraryFile(rid, "everywhere"))), "no library next to the program");
        AssertNoRuntimesFolder(output);
        CommandResult run = Command.Run("dotnet", [Path.Combine(output, "NetThroughLibrary.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Expected(rid), run.StdOut.Trim());
    }

    /// <summary>
    /// The same for dotnet publish -r: the publish folder has the flat library and no
    /// runtimes/ (the same dead copies reached container images too); it runs.
    /// </summary>
    [Fact]
    public void A_rid_specific_publish_through_a_class_library_publishes_only_its_own_rid()
    {
        string rid = HostRidOrSkip();
        string folder = RunFolder("publish-rid");
        CommandResult publish = sdk.Once($"publish NetThroughLibrary -r {rid}", () => sdk.Dotnet("publish", App, "-c", "Release", "-r", rid, "-o", folder));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.True(File.Exists(Path.Combine(folder, CrossPlatformTests.LibraryFile(rid, "everywhere"))), "no library in the publish folder");
        AssertNoRuntimesFolder(folder);
        CommandResult run = Command.Run("dotnet", [Path.Combine(folder, "NetThroughLibrary.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Expected(rid), run.StdOut.Trim());
    }

    /// <summary>
    /// dotnet build -r with -o: the asm projects build into their own folders (each RID apart),
    /// not into the program's -o folder, which gets the program and its flat library only.
    /// Before 0.3.0 the global OutputPath made every RID of the class library's portable asm
    /// build write into the -o folder, and a link read another machine's static library
    /// ("machine type x64 conflicts with arm64").
    /// </summary>
    [Fact]
    public void A_build_into_an_output_folder_keeps_the_asm_builds_apart()
    {
        string rid = HostRidOrSkip();
        string folder = RunFolder("build-o");
        CommandResult build = sdk.Once($"build NetThroughLibrary -r {rid} -o", () => sdk.Dotnet("build", App, "-c", "Release", "-r", rid, "-o", folder));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.True(File.Exists(Path.Combine(folder, CrossPlatformTests.LibraryFile(rid, "everywhere"))), "no library in the -o folder");
        // The static library Everywhere links stays in its own project's folder.
        Assert.Empty(Directory.GetFiles(folder, "*everywherestatic*", SearchOption.AllDirectories));
        CommandResult run = Command.Run("dotnet", [Path.Combine(folder, "NetThroughLibrary.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Expected(rid), run.StdOut.Trim());
    }

    /// <summary>
    /// A single-file publish keeps the asm library beside the bundle, as it keeps a NuGet
    /// package's native library: before 0.3.0 the library lacked AssetType=native, so on
    /// Windows the bundler refused it (NETSDK1236) and it was neither inside nor beside, and the
    /// program failed with DllNotFoundException. No warning, no runtimes/, and it runs.
    /// </summary>
    [Fact]
    public void A_single_file_program_keeps_the_native_library_beside_it()
    {
        string rid = HostRidOrSkip();
        string folder = RunFolder("single-file");
        CommandResult publish = sdk.Once($"publish NetThroughLibrary -r {rid} single-file", () =>
            sdk.Dotnet("publish", App, "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true", "-o", folder));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.DoesNotContain("NETSDK1236", publish.StdOut);
        Assert.True(File.Exists(Path.Combine(folder, CrossPlatformTests.LibraryFile(rid, "everywhere"))), "no library beside the single file");
        AssertNoRuntimesFolder(folder);
        CommandResult run = Command.Run(Path.Combine(folder, AppHost), [], folder);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Expected(rid), run.StdOut.Trim());
    }

    /// <summary>
    /// With IncludeNativeLibrariesForSelfExtract the asm library goes inside the bundle: the
    /// publish folder has no library file, and the bundle copied alone to an empty folder runs
    /// (the host extracts the library at start). Before 0.3.0 a libfoo.so or libfoo.dylib counted
    /// as unknown content and stayed outside, so the bundle alone failed.
    /// </summary>
    [Fact]
    public void A_single_file_program_can_carry_the_native_library_inside()
    {
        string rid = HostRidOrSkip();
        string folder = RunFolder("single-file-inside");
        CommandResult publish = sdk.Once($"publish NetThroughLibrary -r {rid} single-file inside", () =>
            sdk.Dotnet("publish", App, "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", folder));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.DoesNotContain("NETSDK1236", publish.StdOut);
        Assert.False(File.Exists(Path.Combine(folder, CrossPlatformTests.LibraryFile(rid, "everywhere"))), "the library stayed outside the bundle");
        AssertNoRuntimesFolder(folder);

        // Alone: nothing beside the bundle can serve the load (File.Copy keeps the Unix mode).
        string alone = RunFolder("single-file-alone");
        Directory.CreateDirectory(alone);
        File.Copy(Path.Combine(folder, AppHost), Path.Combine(alone, AppHost), overwrite: true);
        CommandResult run = Command.Run(Path.Combine(alone, AppHost), [], alone);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Expected(rid), run.StdOut.Trim());
    }

    /// <summary>
    /// The publish switches on the command line (dotnet publish -p:PublishAot=true,
    /// -p:PublishTrimmed=true) are global properties, which NuGet's restore applies to every
    /// project of the graph. The asm projects ignore them (TreatAsLocalProperty in Sdk.props);
    /// before 0.3.0 the restore stopped in each with NETSDK1207 (AOT) or NETSDK1124 (trimming).
    /// Restore only: that is where it failed, and AOT compilation itself needs the platform's
    /// linker, which is .NET's prerequisite, not AsmSdk's.
    /// </summary>
    [Fact]
    public void The_publish_switches_on_the_command_line_stay_out_of_the_asm_projects()
    {
        string rid = HostRidOrSkip();
        CommandResult restore = sdk.Once($"restore NetThroughLibrary -r {rid} AOT trimmed", () =>
            sdk.Dotnet("restore", App, "-r", rid, "-p:PublishAot=true", "-p:PublishTrimmed=true"));
        Assert.True(restore.ExitCode == 0, restore.Tail());
        Assert.DoesNotContain("NETSDK1207", restore.StdOut);
        Assert.DoesNotContain("NETSDK1124", restore.StdOut);
    }
}
