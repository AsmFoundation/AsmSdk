using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The runtime identifiers of 0.3.0, end to end: one .asmproj built for all seven RIDs
/// (tests/projects/Everywhere, EverywhereStatic, EverywhereLibc, EverywhereLibcLibrary), the
/// .NET project that references it (NetConsumer), its package, and the RID rules' errors.
/// </summary>
/// <remarks>
/// Every RID is built on every build machine and checked with the bundled llvm-readobj: object
/// format, exports and imports. What can run, runs: the build of the machine's own RID, loaded
/// into this test process (libraries) or started (programs), so each CI runner proves its own
/// platform; musl through Docker's alpine image when Docker is there; the portable .NET build
/// with the host's runtime.
/// </remarks>
public sealed class CrossPlatformTests(SdkUnderTest sdk)
{
    /// <summary>Every RID AsmSdk builds, in the order the projects list them.</summary>
    public static readonly TheoryData<string> AllRids =
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "osx-x64", "osx-arm64"];

    /// <summary>What Everywhere's ew_platform() returns per RID (see Everywhere.asmproj).</summary>
    private static readonly Dictionary<string, int> PlatformCode = new()
    {
        ["win-x64"] = 1, ["win-arm64"] = 2, ["linux-x64"] = 3, ["linux-arm64"] = 4,
        ["linux-musl-x64"] = 5, ["osx-x64"] = 6, ["osx-arm64"] = 7,
    };

    /// <summary>What ew_os() returns per RID: 100 + the osname() variant its qualifier picked.</summary>
    private static readonly Dictionary<string, int> OsNameCode = new()
    {
        ["win-x64"] = 111, ["win-arm64"] = 121, ["linux-x64"] = 112, ["linux-arm64"] = 120,
        ["linux-musl-x64"] = 113, ["osx-x64"] = 110, ["osx-arm64"] = 120,
    };

    /// <summary>The object file EverywhereStatic assembles per RID (its qualified variants).</summary>
    private static readonly Dictionary<string, string> OsNameObject = new()
    {
        ["win-x64"] = "osname.win.o", ["win-arm64"] = "osname.win-arm64.o", ["linux-x64"] = "osname.linux.o",
        ["linux-arm64"] = "osname.o", ["linux-musl-x64"] = "osname.linux-musl.o", ["osx-x64"] = "osname.o",
        ["osx-arm64"] = "osname.o",
    };

    /// <summary>llvm-readobj's "Format:" line per RID.</summary>
    private static readonly Dictionary<string, string> Format = new()
    {
        ["win-x64"] = "COFF-x86-64", ["win-arm64"] = "COFF-ARM64", ["linux-x64"] = "elf64-x86-64",
        ["linux-arm64"] = "elf64-littleaarch64", ["linux-musl-x64"] = "elf64-x86-64",
        ["osx-x64"] = "Mach-O 64-bit x86-64", ["osx-arm64"] = "Mach-O arm64",
    };

    /// <summary>The NetConsumer project file.</summary>
    private static readonly string NetConsumer = Repo.TestProjectPath("NetConsumer", "NetConsumer.csproj");

    /// <summary>A shared library's file name per RID: name.dll, libname.dylib or libname.so.</summary>
    /// <param name="rid">The RID.</param>
    /// <param name="name">The AssemblyName.</param>
    /// <returns>The file name.</returns>
    private static string LibraryFile(string rid, string name) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? $"{name}.dll"
        : rid.StartsWith("osx-", StringComparison.Ordinal) ? $"lib{name}.dylib"
        : $"lib{name}.so";

    /// <summary>The build of this test process's own platform, when AsmSdk builds that RID.</summary>
    /// <returns>The host RID, or null on a platform none of the seven covers.</returns>
    private static string? HostBuildRid() => PlatformCode.ContainsKey(Repo.HostRid) ? Repo.HostRid : null;

    /// <summary>Builds Everywhere (and EverywhereStatic through its reference) once.</summary>
    /// <returns>The build's result, asserted successful.</returns>
    private CommandResult BuildEverywhere()
    {
        CommandResult build = sdk.BuildTestProject("Everywhere");
        Assert.True(build.ExitCode == 0, build.Tail());
        return build;
    }

    /// <summary>
    /// One build, seven object formats: PE/COFF x64 and ARM64, ELF x86-64 and AArch64 (glibc and
    /// musl), Mach-O x86-64 and arm64, each named the way its OS names libraries.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void Each_rid_gets_its_own_object_format(string rid)
    {
        BuildEverywhere();
        string library = Repo.TestProjectPath("Everywhere", "bin", "Release", "freestanding", rid, LibraryFile(rid, "everywhere"));
        Assert.True(File.Exists(library), $"{rid}: no {library}");
        Assert.Contains($"Format: {Format[rid]}", sdk.ReadObj(library, "--file-headers"));
    }

    /// <summary>
    /// FUNCTION exports on every OS (the .drectve directive on Windows, global symbols elsewhere,
    /// with Mach-O's underscore), and a LOCAL_FUNCTION is exported nowhere.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void Each_os_exports_the_functions_and_hides_the_local_one(string rid)
    {
        BuildEverywhere();
        string library = Repo.TestProjectPath("Everywhere", "bin", "Release", "freestanding", rid, LibraryFile(rid, "everywhere"));
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            // FUNCTION's .drectve export directive; a LOCAL_FUNCTION has none.
            string exports = sdk.ReadObj(library, "--coff-exports");
            Assert.Contains("Name: ew_add", exports);
            Assert.Contains("Name: ew_platform", exports);
            Assert.DoesNotContain("ew_hidden", exports);
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            // Mach-O: the underscore NASM's --gprefix and asmsdk-gnu.inc add; "Extern" marks an export.
            string symbols = sdk.ReadObj(library, "--symbols");
            Assert.Matches(new Regex(@"Name: _ew_add \(\d+\)\s+Extern"), symbols);
            Assert.DoesNotMatch(new Regex(@"Name: _ew_hidden \(\d+\)\s+Extern"), symbols);
        }
        else
        {
            string dynamic = sdk.ReadObj(library, "--dyn-syms");
            Assert.Contains("ew_add", dynamic);
            Assert.DoesNotContain("ew_hidden", dynamic);
        }
    }

    /// <summary>
    /// The most specific osname.* of EverywhereStatic is assembled for each RID, and nothing else:
    /// the RID-less static library was built for every RID its referencing project asked for.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void File_name_qualifiers_pick_the_source_of_each_rid(string rid)
    {
        BuildEverywhere();
        // The RID-less static library was built for each RID Everywhere asked for.
        string objects = Repo.TestProjectPath("EverywhereStatic", "obj", "Release", "freestanding", rid, "asm", "obj");
        string[] built = Directory.GetFiles(objects).Select(Path.GetFileName).OfType<string>().ToArray();
        Assert.Equal([OsNameObject[rid]], built);
    }

    /// <summary>
    /// The build of this machine's RID loads into this process and answers: ew_add, the RID it
    /// was built for (the platform defines) and the static library's variant (the qualifiers).
    /// On macOS this is also the proof that a dylib with no libSystem dependency loads.
    /// </summary>
    [Fact]
    public void The_hosts_own_build_loads_and_answers()
    {
        BuildEverywhere();
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        (int add, int platform, int os) = CallEverywhere(Repo.TestProjectPath("Everywhere", "bin", "Release", "freestanding", rid, LibraryFile(rid, "everywhere")));
        Assert.Equal(42, add);
        Assert.Equal(PlatformCode[rid], platform);
        Assert.Equal(OsNameCode[rid], os);
    }

    /// <summary>
    /// A libc program links each platform's C library through the SDK's stand-ins: the UCRT on
    /// Windows, libSystem on macOS, glibc or musl (with their loaders) on Linux.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void Each_rid_builds_a_libc_program_against_its_own_c_library(string rid)
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibc");
        Assert.True(build.ExitCode == 0, build.Tail());
        string program = Repo.TestProjectPath("EverywhereLibc", "bin", "Release", "libc", rid,
            rid.StartsWith("win-", StringComparison.Ordinal) ? "everywhere-libc.exe" : "everywhere-libc");
        Assert.True(File.Exists(program), $"{rid}: no {program}");
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            // The UCRT import library; printf itself comes from the SDK's wrapper.
            string imports = sdk.ReadObj(program, "--coff-imports");
            Assert.Contains("Name: ucrtbase.dll", imports);
            Assert.Contains("__stdio_common_vfprintf", imports);
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            Assert.Contains("/usr/lib/libSystem.B.dylib", sdk.ReadObj(program, "--needed-libs"));
        }
        else
        {
            string needed = sdk.ReadObj(program, "--needed-libs");
            Assert.Contains(rid == "linux-musl-x64" ? "libc.musl-x86_64.so.1" : "libc.so.6", needed);
            string interpreter = rid switch
            {
                "linux-musl-x64" => "/lib/ld-musl-x86_64.so.1",
                "linux-arm64" => "/lib/ld-linux-aarch64.so.1",
                _ => "/lib64/ld-linux-x86-64.so.2",
            };
            Assert.Contains(interpreter, sdk.ReadElf(program, "-l"));
        }
    }

    /// <summary>
    /// The libc program of this machine's RID runs: startup code, printf with five arguments,
    /// atexit at exit, and main's return value as the exit code.
    /// </summary>
    [Fact]
    public void The_hosts_own_libc_program_runs()
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibc");
        Assert.True(build.ExitCode == 0, build.Tail());
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no program for this machine ({Repo.HostRid})");
        }

        string program = Repo.TestProjectPath("EverywhereLibc", "bin", "Release", "libc", rid,
            OperatingSystem.IsWindows() ? "everywhere-libc.exe" : "everywhere-libc");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
        }

        CommandResult run = Command.Run(program, [], Repo.Root);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains($"hello from {rid}, 40 + 2 = 42", run.StdOut);
        Assert.Contains("bye from atexit", run.StdOut);
    }

    /// <summary>
    /// The linux-musl-x64 program runs on Alpine (Docker's alpine image), where glibc programs
    /// cannot: musl's SONAME and loader are right. Skipped without Docker for amd64 containers.
    /// </summary>
    [Fact]
    public void The_musl_program_runs_on_alpine()
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibc");
        Assert.True(build.ExitCode == 0, build.Tail());
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !Command.OnPath("docker") || OperatingSystem.IsMacOS())
        {
            Assert.Skip("No Docker for linux/amd64 containers on this machine");
        }

        string folder = Repo.TestProjectPath("EverywhereLibc", "bin", "Release", "libc", "linux-musl-x64");
        CommandResult run = Command.Run("docker", ["run", "--rm", "--platform", "linux/amd64", "-v", $"{folder}:/app:ro", "alpine:3", "/app/everywhere-libc"], Repo.Root);
        if (run.Output.Contains("Cannot connect to the Docker daemon", StringComparison.Ordinal) || run.Output.Contains("docker_engine", StringComparison.Ordinal))
        {
            Assert.Skip("Docker is installed but its daemon is not running");
        }

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("hello from linux-musl-x64, 40 + 2 = 42", run.StdOut);
        Assert.Contains("bye from atexit", run.StdOut);
    }

    /// <summary>
    /// The libc shared library of this machine's RID loads into this process and calls strlen,
    /// malloc, memset and free from the platform's C library.
    /// </summary>
    [Fact]
    public void The_hosts_own_libc_library_calls_its_c_library()
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibcLibrary");
        Assert.True(build.ExitCode == 0, build.Tail());
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        nint library = LoadCopy(Repo.TestProjectPath("EverywhereLibcLibrary", "bin", "Release", "libc", rid, LibraryFile(rid, "everywherelibc")));
        unsafe
        {
            Assert.Equal(8, ((delegate* unmanaged<int>)NativeLibrary.GetExport(library, "elc_len"))());
            Assert.Equal(1, ((delegate* unmanaged<int>)NativeLibrary.GetExport(library, "elc_heap"))());
        }
    }

    /// <summary>
    /// A .NET project that references the seven-RID project gets every RID's library in
    /// runtimes/&lt;rid&gt;/native/, each listed in deps.json, with no setup of its own.
    /// </summary>
    [Fact]
    public void A_portable_dotnet_build_carries_every_rid_in_runtimes_and_deps_json()
    {
        CommandResult build = sdk.Once("build NetConsumer", () => sdk.Dotnet("build", NetConsumer, "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string output = Repo.TestProjectPath("NetConsumer", "bin", "Release", "net10.0");
        string deps = File.ReadAllText(Path.Combine(output, "NetConsumer.deps.json"));
        foreach (string rid in PlatformCode.Keys)
        {
            string relative = $"runtimes/{rid}/native/{LibraryFile(rid, "everywhere")}";
            Assert.True(File.Exists(Path.Combine(output, relative)), $"no {relative}");
            Assert.Contains($"\"{relative}\"", deps);
        }
    }

    /// <summary>
    /// The portable .NET output, run on this machine, loads the library of this machine's RID
    /// (the .NET host reads deps.json's runtimeTargets).
    /// </summary>
    [Fact]
    public void The_portable_dotnet_build_loads_the_hosts_library()
    {
        CommandResult build = sdk.Once("build NetConsumer", () => sdk.Dotnet("build", NetConsumer, "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetConsumer", "bin", "Release", "net10.0", "NetConsumer.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal($"{rid} 42 {PlatformCode[rid]} {OsNameCode[rid]}", run.StdOut.Trim());
    }

    /// <summary>
    /// dotnet build -r of the .NET project builds the asm project for that RID only and copies its
    /// library next to the app, without runtimes/; the app runs.
    /// </summary>
    [Fact]
    public void A_rid_specific_dotnet_build_copies_one_library_next_to_the_app()
    {
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }

        CommandResult build = sdk.Once($"build NetConsumer -r {rid}", () => sdk.Dotnet("build", NetConsumer, "-c", "Release", "-r", rid));
        Assert.True(build.ExitCode == 0, build.Tail());
        string output = Repo.TestProjectPath("NetConsumer", "bin", "Release", "net10.0", rid);
        Assert.True(File.Exists(Path.Combine(output, LibraryFile(rid, "everywhere"))), "no library next to the app");
        Assert.False(Directory.Exists(Path.Combine(output, "runtimes")), "a RID-specific build carries runtimes/");
        CommandResult run = Command.Run("dotnet", [Path.Combine(output, "NetConsumer.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal($"{rid} 42 {PlatformCode[rid]} {OsNameCode[rid]}", run.StdOut.Trim());
    }

    /// <summary>
    /// A RID no asm build matches exactly gets the one NuGet's fallback order picks:
    /// linux-musl-arm64 is served by linux-arm64 (an AArch64 library, flat in the output).
    /// </summary>
    [Fact]
    public void The_rid_graph_picks_the_build_that_serves_an_unlisted_rid()
    {
        // linux-musl-arm64 is no AsmSdk RID; NuGet's graph falls back to linux-arm64.
        CommandResult build = sdk.Once("build NetConsumer -r linux-musl-arm64",
            () => sdk.Dotnet("build", NetConsumer, "-c", "Release", "-r", "linux-musl-arm64", "-v:n"));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains("linux-musl-arm64 is served by its linux-arm64 build", build.StdOut);
        string library = Repo.TestProjectPath("NetConsumer", "bin", "Release", "net10.0", "linux-musl-arm64", "libeverywhere.so");
        Assert.Contains("Format: elf64-littleaarch64", sdk.ReadObj(library, "--file-headers"));
    }

    /// <summary>A RID that nothing in the fallback order serves (win-x86: Windows on 32-bit x86, which .NET builds for and AsmSdk does not) stops the .NET build with ASM0040.</summary>
    [Fact]
    public void A_rid_that_no_build_serves_fails_with_the_lists()
    {
        CommandResult build = sdk.Once("build NetConsumer -r win-x86", () => sdk.Dotnet("build", NetConsumer, "-c", "Release", "-r", "win-x86"));
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("ASM0040", build.StdOut);
    }

    /// <summary>
    /// dotnet pack puts every RID's library under runtimes/&lt;rid&gt;/native/ (and the Windows import
    /// libraries under native/), and the implicit AsmFoundation.Sdk reference is not a dependency.
    /// </summary>
    [Fact]
    public void A_package_carries_every_rid_and_no_sdk_dependency()
    {
        string output = Path.Combine(Repo.Artifacts, "test-packages");
        CommandResult pack = sdk.Once("pack Everywhere", () => sdk.Dotnet("pack", Repo.TestProject("Everywhere"), "-c", "Release", "-o", output));
        Assert.True(pack.ExitCode == 0, pack.Tail());
        string nupkg = Path.Combine(output, "Test.Everywhere.1.0.0.nupkg");
        SortedSet<string> entries = SdkUnderTest.PackageEntries(nupkg);
        foreach (string rid in PlatformCode.Keys)
        {
            Assert.Contains($"runtimes/{rid}/native/{LibraryFile(rid, "everywhere")}", entries);
        }

        // Windows DLLs carry their import library, for asm projects that link them.
        Assert.Contains("native/win-x64/freestanding/everywhere.dll.lib", entries);
        Assert.Contains("native/win-arm64/freestanding/everywhere.dll.lib", entries);

        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        using var reader = new StreamReader(archive.GetEntry("Test.Everywhere.nuspec")!.Open());
        Assert.DoesNotContain("AsmFoundation.Sdk", reader.ReadToEnd());
    }

    /// <summary>Each RID rule stops the build with its own error code (see the test projects' comments).</summary>
    /// <param name="project">The test project that breaks the rule.</param>
    /// <param name="code">The error code it must fail with.</param>
    [Theory]
    [InlineData("RidNone", "ASM0010")]
    [InlineData("RidUnknown", "ASM0011")]
    [InlineData("RidMacExe", "ASM0015")]
    [InlineData("RidTie", "ASM0020")]
    [InlineData("RidArmNasm", "ASM0021")]
    [InlineData("RidNoSource", "ASM0023")]
    [InlineData("RidDuplicate", "ASM0024")]
    public void A_rid_rule_violation_fails_with_its_code(string project, string code)
    {
        CommandResult build = sdk.BuildTestProject(project);
        Assert.True(build.ExitCode != 0, $"{project} built, but must fail: {build.Tail()}");
        Assert.Contains(code, build.StdOut);
    }

    /// <summary>-r with a RID that the project's RuntimeIdentifiers do not list stops with ASM0012.</summary>
    [Fact]
    public void A_rid_the_project_does_not_list_fails()
    {
        CommandResult build = sdk.BuildTestProject("RidMacExe", "-r", "linux-x64");
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("ASM0012", build.StdOut);
    }

    /// <summary>Loads a copy of Everywhere's library into this process and calls its three exports.</summary>
    /// <param name="path">The library.</param>
    /// <returns>ew_add(40, 2), ew_platform() and ew_os().</returns>
    private static (int Add, int Platform, int Os) CallEverywhere(string path)
    {
        nint library = LoadCopy(path);
        unsafe
        {
            var add = (delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(library, "ew_add");
            var platform = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "ew_platform");
            var os = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "ew_os");
            return (add(40, 2), platform(), os());
        }
    }

    /// <summary>
    /// Loads a copy of a native library, from a folder of its own under artifacts/test-run/:
    /// a library loaded from bin/ would stay locked on Windows until the test process ends, and
    /// a later build could not overwrite it. Never unloaded, for the same reason a P/Invoke
    /// library is not: this process may still hold a function pointer into it.
    /// </summary>
    /// <param name="path">The library to copy and load.</param>
    /// <returns>The library handle.</returns>
    private static nint LoadCopy(string path)
    {
        Assert.True(File.Exists(path), $"no {path}");
        string folder = Path.Combine(Repo.Artifacts, "test-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, copy);
        return NativeLibrary.Load(copy);
    }
}
