using System.IO.Compression;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The callers of samples/callers: Crc32C, the assembly library of samples/Crc32C, called from
/// C++, Python (a package whose wheel carries the library), Rust, Go and Zig, each through
/// crc32c.h, the header the SDK writes from the library's PROTO lines. Each caller is built with
/// its own language's tools against this machine's build of the library
/// (bin/Release/freestanding/&lt;host RID&gt;/), run, and must print the same four lines.
/// </summary>
/// <remarks>
/// <para>
/// <c>ASMSDK_TEST_CALLERS</c> says what a missing tool means: <c>auto</c> (the default: each
/// caller runs where its tools are on PATH and is skipped with the reason elsewhere),
/// <c>required</c> (the test fails instead), or <c>none</c> (all are skipped). CI's Linux x64
/// runner says <c>required</c>: its image has g++, Python, Rust and Go, and the workflow installs
/// Zig, so all five run there and cannot be skipped by accident.
/// </para>
/// <para>
/// Windows runs the Python and Rust callers. The C++ and Go callers need a C or C++ compiler on
/// PATH (c++, and cc for cgo), which Windows does not have in its default environment, so they
/// skip there with that reason, as <see cref="CHeaderTests"/> does for C. A tool built for
/// another CPU than the machine's (an x64 Python on an ARM64 machine) is skipped the same way:
/// it could not load this machine's library.
/// </para>
/// <para>
/// Each caller builds in artifacts/test-run/callers/&lt;language&gt;/, so the sample folders stay
/// as they are in the repository.
/// </para>
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture), whose samples build made the library.</param>
public sealed class CallerTests(SdkUnderTest sdk)
{
    /// <summary>
    /// The four lines every caller prints on standard output. The 1 MiB buffer (byte i =
    /// i * 31 mod 251) has the CRC-32C 0xd047105b, so the line checks the value itself, besides
    /// the caller's own check that the CPU's path agrees with the portable one. <c>\r?</c>: Python
    /// on Windows ends its lines with CRLF.
    /// </summary>
    private static readonly Regex[] ExpectedLines =
    [
        new(@"^crc32c\(""123456789""\)\s+0xe3069283\r?$", RegexOptions.Multiline),
        new(@"^in two calls\s+0xe3069283\r?$", RegexOptions.Multiline),
        new(@"^1 MiB\s+0xd047105b\r?$", RegexOptions.Multiline),
        new(@"^path on this CPU\s+[123], \S", RegexOptions.Multiline),
    ];

    /// <summary>
    /// What <c>ASMSDK_TEST_CALLERS</c> asks for: "auto", "required" or "none" (see the class
    /// remarks).
    /// </summary>
    /// <exception cref="InvalidOperationException">The variable holds anything else (thrown by the initializer).</exception>
    private static readonly string Mode = ParseMode(Environment.GetEnvironmentVariable("ASMSDK_TEST_CALLERS"));

    /// <summary>This machine's build of Crc32C: the library, crc32c.h and the Windows import library.</summary>
    private static readonly string LibraryFolder = Repo.Sample("Crc32C", "bin", "Release", "freestanding", Repo.HostRid);

    /// <summary>The library's file name on this OS.</summary>
    private static readonly string LibraryFile = OperatingSystem.IsWindows() ? "crc32c.dll"
        : OperatingSystem.IsMacOS() ? "libcrc32c.dylib"
        : "libcrc32c.so";

    /// <summary>The CPU half of this machine's RID: "x64" or "arm64".</summary>
    private static readonly string HostArch = Repo.HostRid[(Repo.HostRid.IndexOf('-') + 1)..];

    /// <summary>
    /// C++: main.cpp includes crc32c.h and links libcrc32c with a run path, compiled with every
    /// warning as an error, so the header compiles cleanly as C++ (its extern "C" block).
    /// </summary>
    [Fact]
    public void Cpp_includes_the_generated_header_and_links_the_library()
    {
        Require(!OperatingSystem.IsWindows() && Command.OnPath("c++"),
            "No C++ compiler (c++) on PATH, or a Windows machine: Linux and macOS run the C++ caller");
        RequireSamples();
        string work = FreshFolder("cpp");
        string program = Path.Combine(work, "crc32c-cpp");
        CommandResult build = Command.Run("c++",
            ["-std=c++17", "-Wall", "-Wextra", "-Werror", $"-I{LibraryFolder}", Repo.Sample("callers", "cpp", "main.cpp"),
             $"-L{LibraryFolder}", "-lcrc32c", $"-Wl,-rpath,{LibraryFolder}", "-o", program], work);
        Assert.True(build.ExitCode == 0, "the C++ caller did not build: " + build.Tail());
        AssertChecks("C++", Command.Run(program, [], work));
    }

    /// <summary>
    /// Python: the package's wheel, built with this machine's library beside its code, is tagged
    /// py3-none-&lt;this platform&gt; (never any), carries the library at its root, and once installed
    /// into a fresh virtual environment, <c>python -m crc32c_asm</c> passes its checks.
    /// </summary>
    /// <remarks>
    /// pip downloads the build backend (setuptools) from PyPI, so the test needs the network, as
    /// the NuGet restores of the other tests do.
    /// </remarks>
    [Fact]
    public void Python_package_carries_the_library_in_a_platform_wheel()
    {
        string python = OperatingSystem.IsWindows() ? "python" : "python3";
        Require(Command.OnPath(python), $"No Python ({python}) on PATH");
        string? arch = ToolArch(Command.Run(python, ["-c", "import platform; print(platform.machine())"]).StdOut.Trim());
        Require(arch == HostArch, $"The Python on PATH is built for {arch ?? "an unknown CPU"}; this machine's library is {Repo.HostRid}");
        RequireSamples();

        // The package's sources and the library, copied together: the sample folder keeps no
        // binary, and a wheel takes package data from beside the code.
        string work = FreshFolder("python");
        string package = Path.Combine(work, "package");
        CopyFolder(Repo.Sample("callers", "python"), package);
        File.Copy(Path.Combine(LibraryFolder, LibraryFile), Path.Combine(package, "src", "crc32c_asm", LibraryFile));

        string venv = Path.Combine(work, "venv");
        CommandResult created = Command.Run(python, ["-m", "venv", venv], work);
        Assert.True(created.ExitCode == 0, "python -m venv failed: " + created.Tail());
        string venvPython = OperatingSystem.IsWindows() ? Path.Combine(venv, "Scripts", "python.exe") : Path.Combine(venv, "bin", "python");
        string dist = Path.Combine(work, "dist");
        CommandResult wheelBuild = Command.Run(venvPython, ["-m", "pip", "wheel", package, "--no-deps", "-w", dist], work);
        Assert.True(wheelBuild.ExitCode == 0, "pip wheel failed: " + wheelBuild.Tail());

        string wheel = Assert.Single(Directory.GetFiles(dist, "*.whl"));
        Assert.Matches(@"^crc32c_asm-1\.0\.0-py3-none-\w+\.whl$", Path.GetFileName(wheel));
        Assert.DoesNotContain("-any.whl", Path.GetFileName(wheel), StringComparison.Ordinal);
        using (ZipArchive archive = ZipFile.OpenRead(wheel))
        {
            Assert.Contains($"crc32c_asm/{LibraryFile}", archive.Entries.Select(entry => entry.FullName));
            ZipArchiveEntry metadata = Assert.Single(archive.Entries, entry => entry.FullName == "crc32c_asm-1.0.0.dist-info/WHEEL");
            using var reader = new StreamReader(metadata.Open());
            Assert.Contains("Root-Is-Purelib: false", reader.ReadToEnd(), StringComparison.Ordinal);
        }

        CommandResult install = Command.Run(venvPython, ["-m", "pip", "install", "--no-deps", wheel], work);
        Assert.True(install.ExitCode == 0, "pip install failed: " + install.Tail());
        // Run from the work folder, which holds no crc32c_asm/ of its own: what runs is the
        // installed package and the library inside it.
        AssertChecks("Python", Command.Run(venvPython, ["-m", "crc32c_asm"], work));
    }

    /// <summary>
    /// Rust: build.rs links the library from CRC32C_LIB_DIR (on Windows its import library,
    /// crc32c.dll.lib, by its verbatim name), the program declares the header's functions in an
    /// extern block, and it passes its checks. Built --locked against the committed Cargo.lock.
    /// </summary>
    [Fact]
    public void Rust_links_the_library_from_its_build_script()
    {
        Require(Command.OnPath("cargo") && Command.OnPath("rustc"), "No Rust (cargo and rustc) on PATH");
        string? arch = ToolArch(Regex.Match(Command.Run("rustc", ["-vV"]).StdOut, @"^host: (\w+)-", RegexOptions.Multiline).Groups[1].Value);
        Require(arch == HostArch, $"rustc on PATH targets {arch ?? "an unknown CPU"} by default; this machine's library is {Repo.HostRid}");
        RequireSamples();
        string work = FreshFolder("rust");
        var environment = new Dictionary<string, string?>
        {
            ["CARGO_TARGET_DIR"] = Path.Combine(work, "target"),
            ["CRC32C_LIB_DIR"] = LibraryFolder,
        };
        CommandResult build = Command.Run("cargo",
            ["build", "--release", "--locked", "--manifest-path", Repo.Sample("callers", "rust", "Cargo.toml")], work, environment);
        Assert.True(build.ExitCode == 0, "the Rust caller did not build: " + build.Tail());
        string program = Path.Combine(work, "target", "release", OperatingSystem.IsWindows() ? "crc32c-caller.exe" : "crc32c-caller");
        AssertChecks("Rust", Command.Run(program, [], work, RunEnvironment()));
    }

    /// <summary>
    /// Go: cgo includes crc32c.h and links the library, both found through CGO_CFLAGS and
    /// CGO_LDFLAGS (no build folder is written in the sample), and the program passes its checks.
    /// </summary>
    [Fact]
    public void Go_calls_the_library_through_cgo()
    {
        Require(!OperatingSystem.IsWindows() && Command.OnPath("go") && Command.OnPath("cc"),
            "No Go, or no C compiler (cc) for cgo, on PATH, or a Windows machine: Linux and macOS run the Go caller");
        string? arch = ToolArch(Command.Run("go", ["env", "GOARCH"]).StdOut.Trim());
        Require(arch == HostArch, $"The Go on PATH builds for {arch ?? "an unknown CPU"} by default; this machine's library is {Repo.HostRid}");
        RequireSamples();
        string work = FreshFolder("go");
        string program = Path.Combine(work, "crc32c-caller");
        // Go splits the CGO_* variables at spaces and honors quotes, so each flag is quoted
        // whole: a folder with a space in its path stays one argument.
        var environment = new Dictionary<string, string?>
        {
            ["CGO_ENABLED"] = "1",
            ["CGO_CFLAGS"] = $"\"-I{LibraryFolder}\"",
            ["CGO_LDFLAGS"] = $"\"-L{LibraryFolder}\" \"-Wl,-rpath,{LibraryFolder}\"",
            // The sample needs Go 1.21; never download a toolchain in the middle of a test.
            ["GOTOOLCHAIN"] = "local",
        };
        CommandResult build = Command.Run("go", ["build", "-o", program, "."], Repo.Sample("callers", "go"), environment);
        Assert.True(build.ExitCode == 0, "the Go caller did not build: " + build.Tail());
        AssertChecks("Go", Command.Run(program, [], work));
    }

    /// <summary>
    /// Zig: build.zig translates crc32c.h into a module (translate-c) and links the library from
    /// -Dcrc32c, and the program passes its checks. The sample is written for Zig 0.17, whose I/O
    /// and build APIs differ from earlier releases', so another version is skipped (auto) or fails
    /// (required).
    /// </summary>
    [Fact]
    public void Zig_translates_the_header_and_links_the_library()
    {
        Require(Command.OnPath("zig"), "No Zig on PATH");
        string version = Command.Run("zig", ["version"]).StdOut.Trim();
        Require(version.StartsWith("0.17.", StringComparison.Ordinal), $"Zig {version} on PATH; the sample is written for Zig 0.17");
        RequireSamples();
        string work = FreshFolder("zig");
        string prefix = Path.Combine(work, "out");
        // The local cache would otherwise be samples/callers/zig/.zig-cache; Zig 0.17 takes it from
        // the environment only (zig build has no option for it).
        var environment = new Dictionary<string, string?> { ["ZIG_LOCAL_CACHE_DIR"] = Path.Combine(work, "cache") };
        CommandResult build = Command.Run("zig", ["build", "install", $"-Dcrc32c={LibraryFolder}", "--prefix", prefix],
            Repo.Sample("callers", "zig"), environment);
        Assert.True(build.ExitCode == 0, "the Zig caller did not build: " + build.Tail());
        string program = Path.Combine(prefix, "bin", OperatingSystem.IsWindows() ? "crc32c-caller.exe" : "crc32c-caller");
        AssertChecks("Zig", Command.Run(program, [], work, RunEnvironment()));
    }

    /// <summary>Reads <c>ASMSDK_TEST_CALLERS</c> (see the class remarks).</summary>
    /// <param name="value">The variable's value, or null when unset.</param>
    /// <returns>"auto", "required" or "none".</returns>
    /// <exception cref="InvalidOperationException">The value is none of those.</exception>
    private static string ParseMode(string? value) => value switch
    {
        null or "" or "auto" => "auto",
        "required" or "none" => value,
        _ => throw new InvalidOperationException($"ASMSDK_TEST_CALLERS={value}: expected auto, required or none"),
    };

    /// <summary>
    /// Lets a caller test go on when its prerequisite holds; otherwise skips it with the reason,
    /// or fails it when <c>ASMSDK_TEST_CALLERS=required</c>.
    /// </summary>
    /// <param name="holds">Whether the prerequisite is met.</param>
    /// <param name="reason">What is missing, for the skip or failure message.</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the callers are required.</exception>
    private static void Require(bool holds, string reason)
    {
        if (Mode == "none")
        {
            Assert.Skip("ASMSDK_TEST_CALLERS=none");
        }

        if (holds)
        {
            return;
        }

        if (Mode == "required")
        {
            Assert.Fail($"{reason} (ASMSDK_TEST_CALLERS=required)");
        }

        Assert.Skip(reason);
    }

    /// <summary>Fails the calling test at once, with the build log, when the samples did not build.</summary>
    /// <exception cref="Exception">An assertion failure when the samples build failed.</exception>
    private void RequireSamples() =>
        Assert.True(sdk.SamplesBuild.ExitCode == 0, "the samples did not build: " + sdk.SamplesBuild.Tail());

    /// <summary>
    /// Maps a tool's name for its CPU (platform.machine(), a Rust or Go architecture) to the RID's
    /// CPU half.
    /// </summary>
    /// <param name="name">x86_64, AMD64, amd64, aarch64, arm64 or ARM64; anything else is unknown.</param>
    /// <returns>"x64", "arm64", or null for a name it does not know.</returns>
    private static string? ToolArch(string name) => name.ToLowerInvariant() switch
    {
        "x86_64" or "amd64" or "x64" => "x64",
        "aarch64" or "arm64" => "arm64",
        _ => null,
    };

    /// <summary>
    /// The environment a caller runs in. Windows has no run path, so the library's folder goes
    /// first on PATH there; elsewhere the program's run path finds the library and nothing is set.
    /// </summary>
    /// <returns>The variables to set, or null to inherit the test process's environment.</returns>
    private static Dictionary<string, string?>? RunEnvironment() => OperatingSystem.IsWindows()
        ? new Dictionary<string, string?> { ["PATH"] = LibraryFolder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }
        : null;

    /// <summary>
    /// A caller's run passed: exit code 0, the four expected lines (<see cref="ExpectedLines"/>),
    /// and no check marked FAILED.
    /// </summary>
    /// <param name="language">The caller's language, for the failure message.</param>
    /// <param name="run">The caller's run.</param>
    /// <exception cref="Exception">An assertion failure naming the language, with the run's output.</exception>
    private static void AssertChecks(string language, CommandResult run)
    {
        Assert.True(run.ExitCode == 0, $"the {language} caller exited with {run.ExitCode}: {run.Tail()}");
        foreach (Regex line in ExpectedLines)
        {
            Assert.True(line.IsMatch(run.StdOut), $"the {language} caller did not print a line matching {line}: {run.Tail()}");
        }

        Assert.DoesNotContain("FAILED", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>An empty folder for one caller's build under artifacts/test-run/callers/, emptied first.</summary>
    /// <param name="language">The folder's name: the caller's language.</param>
    /// <returns>The folder's full path.</returns>
    /// <exception cref="IOException">A file in the old folder is in use (a caller still running).</exception>
    private static string FreshFolder(string language)
    {
        string folder = Path.Combine(Repo.Artifacts, "test-run", "callers", language);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return Directory.CreateDirectory(folder).FullName;
    }

    /// <summary>
    /// Copies a sample folder, leaving out what building it by hand leaves behind (the entries
    /// of samples/callers/.gitignore), so a developer's own build never reaches the test's.
    /// </summary>
    /// <param name="source">The folder to copy.</param>
    /// <param name="destination">Where the copy goes; created.</param>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    private static void CopyFolder(string source, string destination)
    {
        string[] leftOut = ["build", "dist", "target", ".zig-cache", "zig-out"];
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            // A library a developer copied in by hand: the test copies this machine's own.
            if (name is "libcrc32c.so" or "libcrc32c.dylib" or "crc32c.dll")
            {
                continue;
            }

            File.Copy(file, Path.Combine(destination, name));
        }

        foreach (string folder in Directory.GetDirectories(source))
        {
            string name = Path.GetFileName(folder);
            if (leftOut.Contains(name) || name.EndsWith(".egg-info", StringComparison.Ordinal))
            {
                continue;
            }

            CopyFolder(folder, Path.Combine(destination, name));
        }
    }
}
