using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The runtime identifiers, end to end: one .asmproj built for all eight RIDs
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
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64"];

    /// <summary>What Everywhere's ew_platform() returns per RID (see Everywhere.asmproj); also the .NET program tests' expectation.</summary>
    internal static readonly Dictionary<string, int> PlatformCode = new()
    {
        ["win-x64"] = 1, ["win-arm64"] = 2, ["linux-x64"] = 3, ["linux-arm64"] = 4,
        ["linux-musl-x64"] = 5, ["osx-x64"] = 6, ["osx-arm64"] = 7, ["linux-musl-arm64"] = 8,
    };

    /// <summary>What ew_os() returns per RID: 100 + the osname() variant its qualifier picked.</summary>
    internal static readonly Dictionary<string, int> OsNameCode = new()
    {
        ["win-x64"] = 111, ["win-arm64"] = 121, ["linux-x64"] = 112, ["linux-arm64"] = 120,
        ["linux-musl-x64"] = 113, ["osx-x64"] = 110, ["osx-arm64"] = 120, ["linux-musl-arm64"] = 122,
    };

    /// <summary>The object file EverywhereStatic assembles per RID (its qualified variants).</summary>
    private static readonly Dictionary<string, string> OsNameObject = new()
    {
        ["win-x64"] = "osname.win.o", ["win-arm64"] = "osname.win-arm64.o", ["linux-x64"] = "osname.linux.o",
        ["linux-arm64"] = "osname.o", ["linux-musl-x64"] = "osname.linux-musl.o", ["osx-x64"] = "osname.o",
        ["osx-arm64"] = "osname.o", ["linux-musl-arm64"] = "osname.linux-musl-arm64.o",
    };

    /// <summary>llvm-readobj's "Format:" line per RID.</summary>
    internal static readonly Dictionary<string, string> Format = new()
    {
        ["win-x64"] = "COFF-x86-64", ["win-arm64"] = "COFF-ARM64", ["linux-x64"] = "elf64-x86-64",
        ["linux-arm64"] = "elf64-littleaarch64", ["linux-musl-x64"] = "elf64-x86-64",
        ["osx-x64"] = "Mach-O 64-bit x86-64", ["osx-arm64"] = "Mach-O arm64", ["linux-musl-arm64"] = "elf64-littleaarch64",
    };

    /// <summary>The NetConsumer project file.</summary>
    private static readonly string NetConsumer = Repo.TestProjectPath("NetConsumer", "NetConsumer.csproj");

    /// <summary>A shared library's file name per RID: name.dll, libname.dylib or libname.so.</summary>
    /// <param name="rid">The RID.</param>
    /// <param name="name">The AssemblyName.</param>
    /// <returns>The file name.</returns>
    internal static string LibraryFile(string rid, string name) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? $"{name}.dll"
        : rid.StartsWith("osx-", StringComparison.Ordinal) ? $"lib{name}.dylib"
        : $"lib{name}.so";

    /// <summary>The build of this test process's own platform, when AsmSdk builds that RID.</summary>
    /// <returns>The host RID, or null on a platform none of the eight covers.</returns>
    internal static string? HostBuildRid() => PlatformCode.ContainsKey(Repo.HostRid) ? Repo.HostRid : null;

    /// <summary>Builds Everywhere (and EverywhereStatic through its reference) once.</summary>
    /// <returns>The build's result, asserted successful.</returns>
    private CommandResult BuildEverywhere()
    {
        CommandResult build = sdk.BuildTestProject("Everywhere");
        Assert.True(build.ExitCode == 0, build.Tail());
        return build;
    }

    /// <summary>
    /// One build, eight RIDs in six object formats: PE/COFF x64 and ARM64, ELF x86-64 and AArch64
    /// (each for glibc and musl), Mach-O x86-64 and arm64, each named the way its OS names libraries. The build
    /// says where each went, with .NET's "Project -> path" line per RID.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void Each_rid_gets_its_own_object_format(string rid)
    {
        CommandResult build = BuildEverywhere();
        string library = Repo.TestProjectPath("Everywhere", "bin", "Release", "freestanding", rid, LibraryFile(rid, "everywhere"));
        Assert.True(File.Exists(library), $"{rid}: no {library}");
        Assert.Contains($"Format: {Format[rid]}", sdk.ReadObj(library, "--file-headers"));
        Assert.Contains($"Everywhere -> {library}", build.StdOut);
    }

    /// <summary>
    /// dotnet build -o of an eight-RID project puts each RID in a folder of its own,
    /// &lt;folder&gt;/&lt;rid&gt;/, with the static library it links beside it. -o sets a global
    /// OutputPath, which the .NET SDK does not extend by the RID: before 0.3.0 every inner build
    /// wrote to the one folder, a library of one OS overwrote another's of the same name, and a
    /// link read a static library built for another machine.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void An_output_folder_on_the_command_line_keeps_each_rid_apart(string rid)
    {
        string folder = Path.Combine(Repo.Artifacts, "test-run", "everywhere-o");
        CommandResult build = sdk.Once("build Everywhere -o", () => sdk.Dotnet("build", Repo.TestProject("Everywhere"), "-c", "Release", "-o", folder));
        Assert.True(build.ExitCode == 0, build.Tail());
        string library = Path.Combine(folder, rid, LibraryFile(rid, "everywhere"));
        Assert.True(File.Exists(library), $"{rid}: no {library}");
        Assert.Contains($"Format: {Format[rid]}", sdk.ReadObj(library, "--file-headers"));
        string staticLibrary = rid.StartsWith("win-", StringComparison.Ordinal) ? "everywherestatic.lib" : "libeverywherestatic.a";
        Assert.True(File.Exists(Path.Combine(folder, rid, staticLibrary)), $"{rid}: no {staticLibrary} beside the library");
        // Nothing of any RID directly in the folder: each went to its own.
        Assert.Empty(Directory.GetFiles(folder));
    }

    /// <summary>
    /// FUNCTION exports on every OS (the .drectve directive on Windows, global symbols elsewhere,
    /// with Mach-O's underscore). A LOCAL_FUNCTION is exported nowhere, and neither is the static
    /// library's HIDDEN_FUNCTION osname, which Everywhere calls from another object.
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
            // FUNCTION's .drectve export directive; a LOCAL_FUNCTION and a HIDDEN_FUNCTION have none.
            string exports = sdk.ReadObj(library, "--coff-exports");
            Assert.Contains("Name: ew_add", exports);
            Assert.Contains("Name: ew_platform", exports);
            Assert.DoesNotContain("ew_hidden", exports);
            Assert.DoesNotContain("osname", exports);
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            // Mach-O: the underscore NASM's --gprefix and asmsdk-gnu.inc add; "Extern" marks an
            // export (private_extern became a local symbol of the dylib).
            string symbols = sdk.ReadObj(library, "--symbols");
            Assert.Matches(new Regex(@"Name: _ew_add \(\d+\)\s+Extern"), symbols);
            Assert.DoesNotMatch(new Regex(@"Name: _ew_hidden \(\d+\)\s+Extern"), symbols);
            Assert.DoesNotMatch(new Regex(@"Name: _osname \(\d+\)\s+Extern"), symbols);
        }
        else
        {
            string dynamic = sdk.ReadObj(library, "--dyn-syms");
            Assert.Contains("ew_add", dynamic);
            Assert.DoesNotContain("ew_hidden", dynamic);
            Assert.DoesNotContain("osname", dynamic);
        }
    }

    /// <summary>
    /// The most specific osname.* of EverywhereStatic is assembled for each RID, and nothing else:
    /// the RID-less static library was built for every RID its referencing project asked for.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    /// <remarks>
    /// Objects only: NASM writes its messages beside each object (&lt;object&gt;.messages, -Z), which
    /// the SDK reads to log its warnings with their names as codes.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllRids))]
    public void File_name_qualifiers_pick_the_source_of_each_rid(string rid)
    {
        BuildEverywhere();
        // The RID-less static library was built for each RID Everywhere asked for.
        string objects = Repo.TestProjectPath("EverywhereStatic", "obj", "Release", "freestanding", rid, "asm", "obj");
        string[] built = Directory.GetFiles(objects).Select(Path.GetFileName).OfType<string>()
            .Where(name => !name.EndsWith(".messages", StringComparison.Ordinal)).ToArray();
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

            // main is a FUNCTION, but a program exports nothing without an ExportsFile, so
            // lld-link writes no import library beside it (both happened before
            // ASMSDK_EXECUTABLE).
            Assert.DoesNotContain("Name: main", sdk.ReadObj(program, "--coff-exports"));
            Assert.False(File.Exists(Path.ChangeExtension(program, ".lib")), $"{rid}: an import library beside the program");
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            Assert.Contains("/usr/lib/libSystem.B.dylib", sdk.ReadObj(program, "--needed-libs"));
        }
        else
        {
            string needed = sdk.ReadObj(program, "--needed-libs");
            Assert.Contains(rid switch
            {
                "linux-musl-x64" => "libc.musl-x86_64.so.1",
                "linux-musl-arm64" => "libc.musl-aarch64.so.1",
                _ => "libc.so.6",
            }, needed);
            string interpreter = rid switch
            {
                "linux-musl-x64" => "/lib/ld-musl-x86_64.so.1",
                "linux-musl-arm64" => "/lib/ld-musl-aarch64.so.1",
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
    /// Each musl program runs on Alpine (Docker's alpine image), where glibc programs cannot:
    /// musl's SONAME and loader are right, and the SDK's crt1 and atexit serve musl as they
    /// serve glibc. linux-musl-arm64 runs natively on an ARM64 machine and under QEMU on an x64
    /// one (Docker Desktop includes it); linux-musl-x64 on an x64 machine.
    /// </summary>
    /// <param name="rid">linux-musl-x64 or linux-musl-arm64.</param>
    /// <param name="platform">Docker's platform for that RID's CPU.</param>
    /// <remarks>
    /// Skipped where Docker runs no Linux containers (GitHub's Windows and macOS runners), and
    /// where it cannot run that CPU's containers: an image of the other CPU without QEMU's
    /// binfmt handlers fails even "true" (exec format error), which says nothing about the SDK.
    /// </remarks>
    [Theory]
    [InlineData("linux-musl-x64", "linux/amd64")]
    [InlineData("linux-musl-arm64", "linux/arm64")]
    public void The_musl_program_runs_on_alpine(string rid, string platform)
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibc");
        Assert.True(build.ExitCode == 0, build.Tail());
        if (!Command.OnPath("docker") || Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root).StdOut.Trim() != "linux")
        {
            Assert.Skip("No Docker running Linux containers here");
        }

        if (Command.Run("docker", ["run", "--rm", "--platform", platform, "alpine:3", "true"], Repo.Root).ExitCode != 0)
        {
            Assert.Skip($"Docker here runs no {platform} containers (no emulation for that CPU)");
        }

        string folder = Repo.TestProjectPath("EverywhereLibc", "bin", "Release", "libc", rid);
        CommandResult run = Command.Run("docker", ["run", "--rm", "--platform", platform, "-v", $"{folder}:/app:ro", "alpine:3", "/app/everywhere-libc"], Repo.Root);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains($"hello from {rid}, 40 + 2 = 42", run.StdOut);
        Assert.Contains("bye from atexit", run.StdOut);
    }

    /// <summary>
    /// An ARM64 machine also runs the x64 build of its OS under its x64 emulation: Windows on
    /// Arm runs win-x64 programs as they are, Apple silicon runs osx-x64 ones through Rosetta 2
    /// (arch -x86_64). This is the only run of the osx-x64 build in CI: LLVM publishes no
    /// Intel Mac toolchain, so there is no osx-x64 build machine. Skipped elsewhere, and on a
    /// Mac without Rosetta ("Bad CPU type in executable").
    /// </summary>
    [Fact]
    public void An_arm64_machine_runs_the_x64_build_under_emulation()
    {
        CommandResult build = sdk.BuildTestProject("EverywhereLibc");
        Assert.True(build.ExitCode == 0, build.Tail());
        string? emulated = Repo.HostRid switch
        {
            "win-arm64" => "win-x64",
            "osx-arm64" => "osx-x64",
            _ => null,
        };
        if (emulated is null)
        {
            Assert.Skip($"{Repo.HostRid} runs no x64 build under emulation (Windows on Arm and Apple silicon do)");
        }

        string program = Repo.TestProjectPath("EverywhereLibc", "bin", "Release", "libc", emulated,
            OperatingSystem.IsWindows() ? "everywhere-libc.exe" : "everywhere-libc");
        CommandResult run;
        if (OperatingSystem.IsWindows())
        {
            run = Command.Run(program, [], Repo.Root);
        }
        else
        {
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
            run = Command.Run("arch", ["-x86_64", program], Repo.Root);
            if (run.Output.Contains("Bad CPU type", StringComparison.Ordinal))
            {
                Assert.Skip("Rosetta 2 is not installed on this Mac");
            }
        }

        Assert.Equal(3, run.ExitCode);
        Assert.Contains($"hello from {emulated}, 40 + 2 = 42", run.StdOut);
        Assert.Contains("bye from atexit", run.StdOut);
    }

    /// <summary>
    /// A program exports exactly what its ExportsFile lists, on each OS (a plugin host's API):
    /// plugin_api, and neither main nor not_listed, although all three are FUNCTIONs. On Windows
    /// it also gets its import library, program-exports.lib, for the plugins to link against.
    /// </summary>
    /// <param name="rid">The runtime identifier (one per OS).</param>
    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("osx-x64")]
    public void A_program_exports_what_its_exports_file_lists(string rid)
    {
        CommandResult build = sdk.BuildTestProject("ProgramExports");
        Assert.True(build.ExitCode == 0, build.Tail());
        string folder = Repo.TestProjectPath("ProgramExports", "bin", "Release", "libc", rid);
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            string exports = sdk.ReadObj(Path.Combine(folder, "program-exports.exe"), "--coff-exports");
            Assert.Contains("Name: plugin_api", exports);
            Assert.DoesNotContain("Name: main", exports);
            Assert.DoesNotContain("not_listed", exports);
            Assert.True(File.Exists(Path.Combine(folder, "program-exports.lib")), "no import library for the plugins");
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            // "Extern" marks an exported Mach-O symbol; the others became private extern.
            string symbols = sdk.ReadObj(Path.Combine(folder, "program-exports"), "--symbols");
            Assert.Matches(new Regex(@"Name: _plugin_api \(\d+\)\s+Extern"), symbols);
            Assert.DoesNotMatch(new Regex(@"Name: _main \(\d+\)\s+Extern"), symbols);
            Assert.DoesNotMatch(new Regex(@"Name: _not_listed \(\d+\)\s+Extern"), symbols);
        }
        else
        {
            string dynamic = sdk.ReadObj(Path.Combine(folder, "program-exports"), "--dyn-syms");
            Assert.Contains("plugin_api@@PROGRAM_1.0", dynamic);
            Assert.DoesNotContain("not_listed", dynamic);
            Assert.DoesNotMatch(new Regex(@"Name: main\b"), dynamic);
        }
    }

    /// <summary>
    /// A fixup the loader cannot apply fails the build, although the linker accepted it: a 32-bit
    /// absolute address in a Windows DLL (ASM0080), a fixup in read-only __TEXT on macOS from the
    /// rebase opcodes or from chained fixups (ASM0081). The image is deleted, so a second build
    /// links and fails again instead of finding the output up to date.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    /// <param name="chainedFixups">Whether to link with -fixup_chains (osx-arm64 only).</param>
    /// <param name="code">The expected error code.</param>
    [Theory]
    [InlineData("win-x64", false, "ASM0080")]
    [InlineData("osx-x64", false, "ASM0081")]
    [InlineData("osx-arm64", false, "ASM0081")]
    [InlineData("osx-arm64", true, "ASM0081")]
    public void A_fixup_the_loader_cannot_apply_fails_the_build(string rid, bool chainedFixups, string code)
    {
        string configuration = chainedFixups ? "Chained" : "Release";
        string[] arguments = ["build", Repo.TestProject("ImageFixups"), "-c", configuration, "-r", rid, $"-p:ChainedFixups={chainedFixups}"];
        string image = Repo.TestProjectPath("ImageFixups", "bin", configuration, "freestanding", rid, LibraryFile(rid, "imagefixups"));
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            CommandResult build = sdk.Dotnet(arguments);
            Assert.True(build.ExitCode != 0, $"attempt {attempt} built: {build.Tail()}");
            Assert.Contains($"error {code}", build.StdOut);
            Assert.False(File.Exists(image), $"attempt {attempt} left {image}");
        }
    }

    /// <summary>
    /// A referenced OutputType=Objects project links into its referencing project for each of
    /// its RIDs (0.1.0 and 0.2.0 passed the object paths relative to the wrong folder), and the
    /// object's HIDDEN_FUNCTION stays unexported while the library's FUNCTION is exported.
    /// </summary>
    /// <param name="rid">The runtime identifier (one per OS).</param>
    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("osx-x64")]
    public void An_object_file_project_links_into_its_referencing_project(string rid)
    {
        CommandResult build = sdk.BuildTestProject("ObjectsUser");
        Assert.True(build.ExitCode == 0, build.Tail());
        string library = Repo.TestProjectPath("ObjectsUser", "bin", "Release", "freestanding", rid, LibraryFile(rid, "objectsuser"));
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            string exports = sdk.ReadObj(library, "--coff-exports");
            Assert.Contains("Name: user_seven", exports);
            Assert.DoesNotContain("obj_seven", exports);
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            string symbols = sdk.ReadObj(library, "--symbols");
            Assert.Matches(new Regex(@"Name: _user_seven \(\d+\)\s+Extern"), symbols);
            Assert.DoesNotMatch(new Regex(@"Name: _obj_seven \(\d+\)\s+Extern"), symbols);
        }
        else
        {
            string dynamic = sdk.ReadObj(library, "--dyn-syms");
            Assert.Contains("user_seven", dynamic);
            Assert.DoesNotContain("obj_seven", dynamic);
        }
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
    /// A .NET project that references the eight-RID project gets every RID's library in
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
    /// A .NET app built for a musl RID gets that RID's own build, not a fallback (the build
    /// names no "is served by"), and runs in .NET's Alpine image: the app prints its RID's
    /// line, so the musl build was loaded. linux-musl-arm64 runs under QEMU on an x64 machine.
    /// </summary>
    /// <param name="rid">linux-musl-x64 or linux-musl-arm64.</param>
    /// <param name="platform">Docker's platform for that RID's CPU.</param>
    /// <param name="image">The .NET runtime image on Alpine for that CPU.</param>
    /// <remarks>
    /// Skipped where Docker runs no Linux containers, or no containers of that CPU (see
    /// <see cref="The_musl_program_runs_on_alpine"/>).
    /// </remarks>
    [Theory]
    [InlineData("linux-musl-x64", "linux/amd64", "mcr.microsoft.com/dotnet/runtime:10.0-alpine")]
    [InlineData("linux-musl-arm64", "linux/arm64", "mcr.microsoft.com/dotnet/runtime:10.0-alpine-arm64v8")]
    public void A_dotnet_app_for_a_musl_rid_gets_its_own_build_and_runs_on_alpine(string rid, string platform, string image)
    {
        CommandResult build = sdk.Once($"build NetConsumer -r {rid}", () => sdk.Dotnet("build", NetConsumer, "-c", "Release", "-r", rid, "-v:n"));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain($"{rid} is served by", build.StdOut);
        string output = Repo.TestProjectPath("NetConsumer", "bin", "Release", "net10.0", rid);
        Assert.Contains($"Format: {Format[rid]}", sdk.ReadObj(Path.Combine(output, "libeverywhere.so"), "--file-headers"));
        if (!Command.OnPath("docker") || Command.Run("docker", ["info", "--format", "{{.OSType}}"], Repo.Root).StdOut.Trim() != "linux")
        {
            Assert.Skip("No Docker running Linux containers here");
        }

        if (Command.Run("docker", ["run", "--rm", "--platform", platform, "alpine:3", "true"], Repo.Root).ExitCode != 0)
        {
            Assert.Skip($"Docker here runs no {platform} containers (no emulation for that CPU)");
        }

        CommandResult run = Command.Run("docker", ["run", "--rm", "--platform", platform, "-v", $"{output}:/app:ro", image, "dotnet", "/app/NetConsumer.dll"], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal($"{rid} 42 {PlatformCode[rid]} {OsNameCode[rid]}", run.StdOut.Trim());
    }

    /// <summary>
    /// A RID no asm build matches exactly gets the one NuGet's fallback order picks:
    /// linux-bionic-arm64 (Android's C library, which .NET builds for and AsmSdk does not) is
    /// served by linux-arm64 (an AArch64 library, flat in the output). Until linux-musl-arm64
    /// became an AsmSdk RID, this test used it; it now gets its own build.
    /// </summary>
    [Fact]
    public void The_rid_graph_picks_the_build_that_serves_an_unlisted_rid()
    {
        // .NET's graph: linux-bionic-arm64 -> linux-bionic, linux-arm64, ...
        CommandResult build = sdk.Once("build NetConsumer -r linux-bionic-arm64",
            () => sdk.Dotnet("build", NetConsumer, "-c", "Release", "-r", "linux-bionic-arm64", "-v:n"));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains("linux-bionic-arm64 is served by its linux-arm64 build", build.StdOut);
        string library = Repo.TestProjectPath("NetConsumer", "bin", "Release", "net10.0", "linux-bionic-arm64", "libeverywhere.so");
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
    /// An asm library that says OptionalOnUnlistedRuntimes (tests/projects/OptionalRid, built for
    /// linux-musl-arm64 alone, which serves no CI machine) is left out of a .NET build for this
    /// machine's RID: the build succeeds with the warning ASM0049, no library is copied, and the
    /// app runs and copes ("optionalrid: absent"), which is what the opt-in promises. In a
    /// configuration of its own (OptionalRid), apart from the other NetOptionalRid builds.
    /// </summary>
    [Fact]
    public void An_optional_asm_library_is_left_out_of_a_rid_it_does_not_build()
    {
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds nothing for this machine ({Repo.HostRid})");
        }

        string project = Repo.TestProjectPath("NetOptionalRid", "NetOptionalRid.csproj");
        CommandResult build = sdk.Dotnet("build", project, "-c", "OptionalRid", "-r", rid, "-p:UseAppHost=false");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains("ASM0049", build.StdOut);
        Assert.Contains("OptionalRid.asmproj builds no runtime identifier that " + rid + " can use", build.StdOut);
        string output = Repo.TestProjectPath("NetOptionalRid", "bin", "OptionalRid", "net10.0", rid);
        // The library's files on any OS: optionalrid.dll, liboptionalrid.so or .dylib. Not a
        // "*optionalrid*" pattern: that matches the app's own NetOptionalRid.dll.
        Assert.DoesNotContain(Directory.GetFiles(output, "*", SearchOption.AllDirectories).Select(Path.GetFileName),
            name => name!.StartsWith("optionalrid.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("liboptionalrid.", StringComparison.OrdinalIgnoreCase));
        CommandResult run = Command.Run("dotnet", [Path.Combine(output, "NetOptionalRid.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("optionalrid: absent", run.StdOut.Trim());
    }

    /// <summary>
    /// Without the opt-in (OptionalOnUnlistedRuntimes=false on the command line, which reaches
    /// the asm project as a global property), the same build still stops with ASM0040, and the
    /// message now names the opt-in for code that copes without the library.
    /// </summary>
    [Fact]
    public void Without_the_opt_in_a_missing_rid_still_stops_the_build()
    {
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds nothing for this machine ({Repo.HostRid})");
        }

        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("NetOptionalRid", "NetOptionalRid.csproj"), "-c", "OptionalRidStrict",
            "-r", rid, "-p:UseAppHost=false", "-p:OptionalOnUnlistedRuntimes=false");
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("ASM0040", build.StdOut);
        Assert.DoesNotContain("ASM0049", build.StdOut);
        Assert.Contains("OptionalOnUnlistedRuntimes=true in OptionalRid.asmproj turns this into a warning", build.StdOut);
    }

    /// <summary>
    /// For the RID it does build (linux-musl-arm64), an optional asm library is delivered like any
    /// other: its library is copied next to the app, and no ASM0049 is raised.
    /// </summary>
    [Fact]
    public void An_optional_asm_library_is_delivered_for_the_rid_it_builds()
    {
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("NetOptionalRid", "NetOptionalRid.csproj"), "-c", "OptionalRid",
            "-r", "linux-musl-arm64", "-p:UseAppHost=false");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("ASM0049", build.StdOut);
        string library = Repo.TestProjectPath("NetOptionalRid", "bin", "OptionalRid", "net10.0", "linux-musl-arm64", "liboptionalrid.so");
        Assert.True(File.Exists(library), $"no {library}: {build.Tail()}");
        Assert.Contains("Format: elf64-littleaarch64", sdk.ReadObj(library, "--file-headers"));
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

    /// <summary>
    /// Each RID rule stops the build with its own error code (see the test projects' comments).
    /// ASM0010 (no RID at all) has no row: it now happens only on a build machine that is not one
    /// of the eight RIDs, which no CI runner is; RidNone builds for this machine instead (below).
    /// </summary>
    /// <param name="project">The test project that breaks the rule.</param>
    /// <param name="code">The error code it must fail with.</param>
    [Theory]
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

    /// <summary>
    /// A project that lists no RuntimeIdentifiers (RidNone, as a new project or a template's)
    /// builds for the build machine's own RID, and for nothing else; a build, the dev loop,
    /// warns about nothing. 0.3.0 stopped with ASM0010 here.
    /// </summary>
    [Fact]
    public void A_project_without_rids_builds_for_this_machine()
    {
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds nothing for this machine ({Repo.HostRid}): RidNone stops with ASM0010 here");
        }

        CommandResult build = sdk.BuildTestProject("RidNone");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("ASM0013", build.StdOut);
        string folder = Repo.TestProjectPath("RidNone", "bin", "Release", "freestanding");
        Assert.Equal([rid], Directory.GetDirectories(folder).Select(Path.GetFileName));
        string library = Path.Combine(folder, rid, LibraryFile(rid, "ridnone"));
        Assert.Contains($"Format: {Format[rid]}", sdk.ReadObj(library, "--file-headers"));
    }

    /// <summary>
    /// dotnet pack of a project without RuntimeIdentifiers warns ASM0013, naming the one RID the
    /// package then carries (the build machine's) and the fix; the package holds that RID alone.
    /// A warning, not an error, even under the test projects' TreatWarningsAsErrors (MSBuild's
    /// own warnings are not the compiler's), and NoWarn silences it like any MSBuild warning.
    /// </summary>
    [Fact]
    public void Packing_a_project_without_rids_warns_that_it_carries_one()
    {
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds nothing for this machine ({Repo.HostRid})");
        }

        string output = Path.Combine(Repo.Artifacts, "test-packages", "ridnone");
        CommandResult pack = sdk.Once("pack RidNone", () => sdk.Dotnet("pack", Repo.TestProject("RidNone"), "-c", "Release", "-o", output));
        Assert.True(pack.ExitCode == 0, pack.Tail());
        Assert.Contains("warning ASM0013", pack.StdOut);
        Assert.Contains($"so this pack carries it for {rid} alone", pack.StdOut);
        Assert.Contains("<RuntimeIdentifiers>", pack.StdOut);
        SortedSet<string> entries = SdkUnderTest.PackageEntries(Path.Combine(output, "ridnone.1.0.0.nupkg"));
        Assert.Equal([rid], entries.Where(e => e.StartsWith("runtimes/", StringComparison.Ordinal)).Select(e => e.Split('/')[1]).Distinct());

        string quiet = Path.Combine(Repo.Artifacts, "test-packages", "ridnone-nowarn");
        CommandResult silenced = sdk.Dotnet("pack", Repo.TestProject("RidNone"), "-c", "Release", "-o", quiet, "-p:NoWarn=ASM0013");
        Assert.True(silenced.ExitCode == 0, silenced.Tail());
        Assert.DoesNotContain("ASM0013", silenced.StdOut);
    }

    /// <summary>
    /// -r comes before the build machine's default: a RID-less project built with -r for another
    /// RID than this machine's builds that RID alone. In a configuration of its own
    /// (ExplicitRid), so that RidNone's Release folder keeps only this machine's RID for
    /// <see cref="A_project_without_rids_builds_for_this_machine"/>.
    /// </summary>
    [Fact]
    public void A_rid_given_with_r_comes_before_the_default()
    {
        string other = Repo.HostRid == "linux-x64" ? "win-x64" : "linux-x64";
        CommandResult build = sdk.Dotnet("build", Repo.TestProject("RidNone"), "-c", "ExplicitRid", "-r", other);
        Assert.True(build.ExitCode == 0, build.Tail());
        string folder = Repo.TestProjectPath("RidNone", "bin", "ExplicitRid", "freestanding");
        Assert.Equal([other], Directory.GetDirectories(folder).Select(Path.GetFileName));
        Assert.Contains($"Format: {Format[other]}", sdk.ReadObj(Path.Combine(folder, other, LibraryFile(other, "ridnone")), "--file-headers"));
    }

    /// <summary>
    /// A .NET project that names the RIDs of a RID-less asm project on its ProjectReference
    /// (tests/projects/NetRidChoice: RuntimeIdentifiers="linux-x64;osx-arm64") gets exactly those
    /// two libraries under runtimes/, whatever this machine is, and the asm project builds those
    /// two RIDs alone: the reference's choice comes before the default. A portable publish of it
    /// raises no ASM0013, the reference having chosen, and carries both. In a configuration of its
    /// own (RidChoice), for the reason <see cref="A_rid_given_with_r_comes_before_the_default"/>
    /// gives.
    /// </summary>
    [Fact]
    public void A_dotnet_reference_names_the_rids_of_a_rid_less_project()
    {
        string project = Repo.TestProjectPath("NetRidChoice", "NetRidChoice.csproj");
        CommandResult build = sdk.Dotnet("build", project, "-c", "RidChoice");
        Assert.True(build.ExitCode == 0, build.Tail());
        string runtimes = Repo.TestProjectPath("NetRidChoice", "bin", "RidChoice", "net10.0", "runtimes");
        Assert.Equal(["linux-x64", "osx-arm64"], Directory.GetDirectories(runtimes).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(runtimes, "linux-x64", "native", "libridnone.so")), "no linux-x64 library");
        Assert.True(File.Exists(Path.Combine(runtimes, "osx-arm64", "native", "libridnone.dylib")), "no osx-arm64 library");
        Assert.Equal(["linux-x64", "osx-arm64"],
            Directory.GetDirectories(Repo.TestProjectPath("RidNone", "bin", "RidChoice", "freestanding")).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        string output = Path.Combine(Repo.Artifacts, "test-run", "rid-choice-publish");
        CommandResult publish = sdk.Dotnet("publish", project, "-c", "RidChoice", "-o", output);
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.DoesNotContain("ASM0013", publish.StdOut);
        Assert.True(File.Exists(Path.Combine(output, "runtimes", "osx-arm64", "native", "libridnone.dylib")), publish.Tail());
    }

    /// <summary>
    /// A .NET library packed with a RID-less asm project embedded (PrivateAssets="all",
    /// tests/projects/NetRidDefaultPack) carries the build machine's RID alone, and the consumer
    /// side says so: ASM0013 for "this portable pack of NetRidDefaultPack", naming the fix. The
    /// package holds that one RID's library. (RidNone, built for the pack, warns ASM0013 too:
    /// dotnet pack's _IsPacking reaches it; both messages name the same fix.)
    /// </summary>
    [Fact]
    public void A_dotnet_pack_that_embeds_a_rid_less_project_warns_that_it_carries_one()
    {
        string? rid = HostBuildRid();
        if (rid is null)
        {
            Assert.Skip($"AsmSdk builds nothing for this machine ({Repo.HostRid})");
        }

        string output = Path.Combine(Repo.Artifacts, "test-packages", "rid-default-pack");
        CommandResult pack = sdk.Dotnet("pack", Repo.TestProjectPath("NetRidDefaultPack", "NetRidDefaultPack.csproj"), "-c", "RidPack", "-o", output);
        Assert.True(pack.ExitCode == 0, pack.Tail());
        Assert.Contains("RidNone lists no RuntimeIdentifiers, so this portable pack of NetRidDefaultPack carries its library for the build machine's platform alone", pack.StdOut);
        SortedSet<string> entries = SdkUnderTest.PackageEntries(Path.Combine(output, "NetRidDefaultPack.1.0.0.nupkg"));
        Assert.Contains($"runtimes/{rid}/native/{LibraryFile(rid, "ridnone")}", entries);
        Assert.Equal([rid], entries.Where(e => e.StartsWith("runtimes/", StringComparison.Ordinal)).Select(e => e.Split('/')[1]).Distinct());
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
    internal static nint LoadCopy(string path)
    {
        Assert.True(File.Exists(path), $"no {path}");
        string folder = Path.Combine(Repo.Artifacts, "test-run", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, copy);
        return NativeLibrary.Load(copy);
    }
}
