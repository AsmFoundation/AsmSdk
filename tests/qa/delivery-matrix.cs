// =============================================================================================
// delivery-matrix.cs: the delivery QA matrix for .NET consumers of asm libraries. It proves
// that every way a .NET project gets an asm library delivers the right binary for every target
// platform, in build and publish, in every BinariesCopyPath layout, by building, inspecting
// and RUNNING the result. A .NET 10 file-based app, like toolchain/fetch.cs. It is the C# port
// of the script that proved AsmSdk 0.3.0 before its release (84/84 on 2026-11-20, which found
// the private-reference bug fixed in ccbef3b).
//
// Usage (from the repository root):
//   dotnet run tests/qa/delivery-matrix.cs -- [--only TEXT] [--list] [--no-prepare]
//     --only TEXT    run only the cases whose name contains TEXT
//     --list         print the cases this machine would run, and exit
//     --no-prepare   reuse the packages and the NuGet cache of the previous run
//
// Prerequisites:
//   - this machine's toolchain, staged: dotnet run toolchain/fetch.cs -- --rid <rid>;
//   - Docker with Linux containers, to run the Linux builds (the ARM64 ones under QEMU, which
//     Docker Desktop includes; on a Linux machine, register qemu-user-static's binfmt
//     handlers). Without Docker the Linux builds are still built and inspected, not run;
//   - NativeAOT's platform linker (Visual Studio's C++ tools on Windows, clang elsewhere): the
//     NativeAOT cases are reported as skipped without it, as the test suite does;
//   - not at the same time as tests/AsmSdk.Tests: both build tests/projects in place, and both
//     pack the test packages into artifacts/packages.
//
// The six delivery paths, all over tests/projects/Everywhere (one shared library for every RID,
// whose exports tell which RID's build was loaded):
//   project              NetLayout -> Everywhere.asmproj
//   through-library      NetLayoutThrough -> NetLayoutLibrary -> Everywhere.asmproj
//   private-library      the same, the library's asm reference PrivateAssets="all"
//   asm-package          NetAsmPackage -> the asm package Test.Everywhere
//   embedding-package    NetLayoutPackage -> Test.NetLayoutLibrary 1.0.0 (the binaries inside)
//   dependency-package   NetLayoutPackage -> Test.NetLayoutLibrary 2.0.0 -> Test.Everywhere
//
// The cases: portable builds of every path in three layouts; builds for each RID of three
// paths in two layouts; self-contained publishes of four paths for each RID this machine can
// run; single-file publishes (direct and private) for this machine and linux-x64; NativeAOT
// publishes of three paths for this machine, and a negative control: NativeAOT with the
// generated loader turned off, which must fail with DllNotFoundException (proving that the
// passing NativeAOT cases depend on the loader). On a Windows x64 machine with Docker: 84.
//
// Each case is built or published into artifacts/qa/delivery/<case>/ and checked four ways:
//   files    exactly the expected builds of Everywhere's library, at the layout's place: every
//            RID in a portable build; one, and no other, for -r. A private library's program
//            has no AsmSdk targets to keep one RID, so it gets every RID the way the library
//            laid them out;
//   format   llvm-readobj's object format and CPU of each file, which must be its RID's;
//   deps     the program's deps.json names each file as a localPath, wherever the output has
//            a deps.json (a single file bundles it, NativeAOT has none) and the program has
//            AsmSdk's targets (not behind a private library);
//   run      wherever this machine runs the RID (natively, or in a container): the program
//            prints "<rid> 42 <ew_platform()> <ew_os()>", which must be the RID's own line.
// Results: artifacts/qa/delivery/results.json (every detail) and results.md (the summary).
// Exit code: 0 when no case failed (skips allowed), 1 otherwise, 2 on a usage error.
// =============================================================================================
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

return DeliveryMatrix.Main(args);

/// <summary>The command line of the delivery QA matrix; see the file header.</summary>
internal static class DeliveryMatrix
{
    /// <summary>Parses the arguments, prepares the packages, runs the cases and writes the report.</summary>
    /// <param name="args">The command line (see the file header).</param>
    /// <returns>The exit code: 0 when no case failed, 1 when one did or preparing failed, 2 on a usage error.</returns>
    public static int Main(string[] args)
    {
        string? only = null;
        bool list = false;
        bool prepare = true;
        for (int i = 0; i < args.Length; i++)
        {
            // --only takes a value; a missing one is a usage error, not an index error.
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--only" when value is not null: only = value; i++; break;
                case "--list": list = true; break;
                case "--no-prepare": prepare = false; break;
                default:
                    Console.Error.WriteLine($"usage: dotnet run tests/qa/delivery-matrix.cs -- [--only TEXT] [--list] [--no-prepare] (bad argument: {args[i]})");
                    return 2;
            }
        }

        try
        {
            var matrix = new Matrix(Path.GetFullPath(Path.Combine(ThisDirectory(), "..", "..")));
            List<Case> cases = matrix.Cases().Where(c => only is null || c.Name.Contains(only, StringComparison.Ordinal)).ToList();
            if (list)
            {
                foreach (Case c in cases)
                {
                    Console.WriteLine($"{c.Name}  runs on: {(c.RunOn.Length == 0 ? "-" : string.Join(", ", c.RunOn))}");
                }

                return 0;
            }

            Console.WriteLine($"{cases.Count} cases on {matrix.HostRid}; runnable here: {string.Join(", ", matrix.Runnable)}{(matrix.HasDocker ? "" : " (no Docker: Linux builds are not run)")}");
            if (prepare)
            {
                matrix.Prepare();
            }

            var results = new List<Result>();
            foreach (Case c in cases)
            {
                Result result = matrix.Check(c);
                results.Add(result);
                Console.WriteLine(result.Line());
                // Written after every case, so a stopped run still leaves what it proved.
                matrix.WriteReport(results);
            }

            int failed = results.Count(r => r.Status == Status.Fail);
            int skipped = results.Count(r => r.Status == Status.Skip);
            Console.WriteLine($"{results.Count - failed - skipped}/{results.Count} passed, {skipped} skipped, {failed} failed: {Path.Combine(matrix.Output, "results.md")}");
            return failed == 0 ? 0 : 1;
        }
        catch (MatrixException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>The folder of this source file (a file-based app runs from a build folder elsewhere).</summary>
    /// <param name="path">Filled in by the compiler.</param>
    /// <returns>The folder: tests/qa.</returns>
    private static string ThisDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

/// <summary>
/// One case of the matrix: how to build or publish, and where to run the result.
/// </summary>
/// <param name="Name">The case's name, also its output folder's.</param>
/// <param name="Path">The delivery path (a key of <see cref="Matrix.Paths"/>).</param>
/// <param name="Verb">build or publish.</param>
/// <param name="Rid">The RID for -r, or null for a portable build.</param>
/// <param name="Layout">The BinariesCopyPath value, or "" for the default.</param>
/// <param name="Extra">More arguments for dotnet (--self-contained, -p:PublishAot=true, ...).</param>
/// <param name="RunOn">The RIDs to run the result as (only runnable ones are listed).</param>
/// <param name="SelfContained">Whether the result runs without a .NET runtime (the container image differs).</param>
/// <param name="Executable">Whether the result is started as its own executable rather than with dotnet.</param>
/// <param name="ExpectRunFailure">The negative control: the run must fail with DllNotFoundException.</param>
internal sealed record Case(string Name, string Path, string Verb, string? Rid, string Layout, string[] Extra, string[] RunOn,
    bool SelfContained = false, bool Executable = false, bool ExpectRunFailure = false);

/// <summary>A delivery path: the program to build and how its layout is set.</summary>
/// <param name="Project">The program's project, relative to the repository root.</param>
/// <param name="Assembly">The program's assembly name (its .dll, .exe and deps.json).</param>
/// <param name="LayoutProperty">The property the test project turns into BinariesCopyPath.</param>
/// <param name="Extra">Arguments every case of the path adds.</param>
internal sealed record DeliveryPath(string Project, string Assembly, string LayoutProperty, string[] Extra);

/// <summary>The containers that run one Linux RID.</summary>
/// <param name="Dependent">The image for a framework-dependent program (it has the .NET runtime).</param>
/// <param name="SelfContained">The image for a self-contained program (the runtime's dependencies only).</param>
/// <param name="Platform">Docker's platform, so either kind of machine runs it (natively or under QEMU).</param>
internal sealed record LinuxImages(string Dependent, string SelfContained, string Platform);

/// <summary>How a case ended.</summary>
internal enum Status
{
    /// <summary>Every check passed.</summary>
    Pass,

    /// <summary>The case could not be run here (no NativeAOT linker); not a failure.</summary>
    Skip,

    /// <summary>A check failed; <see cref="Result.Problems"/> says which.</summary>
    Fail,
}

/// <summary>What one case found.</summary>
/// <param name="Case">The case.</param>
/// <param name="Seconds">How long its build or publish took.</param>
internal sealed record Result(Case Case, int Seconds)
{
    /// <summary>The outcome; <see cref="Status.Fail"/> as soon as a problem is recorded.</summary>
    public Status Status { get; private set; } = Status.Pass;

    /// <summary>What failed, one entry per failed check.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>The builds of Everywhere's library found in the output, relative, with '/'.</summary>
    public List<string> Files { get; } = [];

    /// <summary>llvm-readobj's format of each RID's file.</summary>
    public SortedDictionary<string, string> Formats { get; } = new(StringComparer.Ordinal);

    /// <summary>Each Everywhere asset of deps.json ("library:asset") with its localPath.</summary>
    public SortedDictionary<string, string> Deps { get; } = new(StringComparer.Ordinal);

    /// <summary>The first output line of each run, by RID.</summary>
    public SortedDictionary<string, string> Runs { get; } = new(StringComparer.Ordinal);

    /// <summary>Why the case was skipped, when it was.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Records a failed check.</summary>
    /// <param name="problem">What failed.</param>
    /// <returns>This result, for chaining.</returns>
    public Result Fail(string problem)
    {
        Problems.Add(problem);
        Status = Status.Fail;
        return this;
    }

    /// <summary>Marks the case skipped: it could not run here, which is not a failure.</summary>
    /// <param name="reason">Why.</param>
    /// <returns>This result, for chaining.</returns>
    public Result Skip(string reason)
    {
        SkipReason = reason;
        Status = Status.Skip;
        return this;
    }

    /// <summary>The console line for this result.</summary>
    /// <returns>Status, name, time, runs and the first problems (or the skip reason).</returns>
    public string Line()
    {
        string runs = string.Join(", ", Runs.Select(r => $"{r.Key}: {r.Value}"));
        string detail = Status switch
        {
            Status.Skip => $"skipped: {SkipReason}",
            Status.Fail => string.Join(" | ", Problems.Take(2)),
            _ => runs,
        };
        string line = $"{Status.ToString().ToUpperInvariant(),-4} {Case.Name,-58} {Seconds,4}s {detail}";
        return line.Length > 320 ? line[..320] : line;
    }

    /// <summary>The result as JSON, for results.json.</summary>
    /// <returns>A JSON object with the case, the outcome and every finding.</returns>
    public JsonObject ToJson() => new()
    {
        ["case"] = Case.Name,
        ["path"] = Case.Path,
        ["verb"] = Case.Verb,
        ["rid"] = Case.Rid ?? "portable",
        ["layout"] = Case.Layout.Length == 0 ? "default" : Case.Layout,
        ["extra"] = string.Join(' ', Case.Extra),
        ["status"] = Status.ToString().ToLowerInvariant(),
        ["seconds"] = Seconds,
        ["skipReason"] = SkipReason,
        ["problems"] = new JsonArray(Problems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["files"] = new JsonArray(Files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        ["formats"] = ToJsonObject(Formats),
        ["deps"] = ToJsonObject(Deps),
        ["runs"] = ToJsonObject(Runs),
    };

    /// <summary>A string map as a JSON object.</summary>
    /// <param name="map">The map.</param>
    /// <returns>The object.</returns>
    private static JsonObject ToJsonObject(SortedDictionary<string, string> map)
    {
        var json = new JsonObject();
        foreach (KeyValuePair<string, string> entry in map)
        {
            json[entry.Key] = entry.Value;
        }

        return json;
    }
}

/// <summary>A preparation step failed: the matrix cannot run (missing toolchain, a pack that failed, a wrong package).</summary>
/// <param name="message">What failed, with what to do when there is something to do.</param>
internal sealed class MatrixException(string message) : Exception(message);

/// <summary>The matrix: its cases, the preparation, the checks and the report.</summary>
internal sealed class Matrix
{
    /// <summary>Every RID AsmSdk builds: a portable build of Everywhere holds one library for each.</summary>
    private static readonly string[] Rids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "osx-x64", "osx-arm64"];

    /// <summary>What Everywhere's ew_platform() returns for each RID: the second number the programs print.</summary>
    private static readonly Dictionary<string, int> PlatformCode = new(StringComparer.Ordinal)
    {
        ["win-x64"] = 1, ["win-arm64"] = 2, ["linux-x64"] = 3, ["linux-arm64"] = 4, ["linux-musl-x64"] = 5, ["osx-x64"] = 6, ["osx-arm64"] = 7,
    };

    /// <summary>
    /// The "Format:" llvm-readobj prints for each RID's library: object format and CPU in its
    /// own words ("Mach-O arm64" has no "64-bit", unlike x86-64's; a musl build is ELF x86-64
    /// like glibc's, the C library being the difference, not the format).
    /// </summary>
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["win-x64"] = "COFF-x86-64", ["win-arm64"] = "COFF-ARM64",
        ["linux-x64"] = "elf64-x86-64", ["linux-musl-x64"] = "elf64-x86-64", ["linux-arm64"] = "elf64-littleaarch64",
        ["osx-x64"] = "Mach-O 64-bit x86-64", ["osx-arm64"] = "Mach-O arm64",
    };

    /// <summary>The containers each Linux RID runs in.</summary>
    private static readonly Dictionary<string, LinuxImages> Containers = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = new("mcr.microsoft.com/dotnet/runtime:10.0", "mcr.microsoft.com/dotnet/runtime-deps:10.0", "linux/amd64"),
        ["linux-musl-x64"] = new("mcr.microsoft.com/dotnet/runtime:10.0-alpine", "mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine", "linux/amd64"),
        ["linux-arm64"] = new("mcr.microsoft.com/dotnet/runtime:10.0-noble-arm64v8", "mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-arm64v8", "linux/arm64"),
    };

    /// <summary>The six delivery paths, by name (see the file header).</summary>
    public static readonly Dictionary<string, DeliveryPath> Paths = new(StringComparer.Ordinal)
    {
        ["project"] = new("tests/projects/NetLayout/NetLayout.csproj", "NetLayout", "NetLayoutReference", []),
        ["through-library"] = new("tests/projects/NetLayoutThrough/NetLayoutThrough.csproj", "NetLayoutThrough", "NetLayoutLibraryReference", []),
        ["private-library"] = new("tests/projects/NetLayoutThrough/NetLayoutThrough.csproj", "NetLayoutThrough", "NetLayoutLibraryReference", ["-p:NetLayoutLibraryPrivate=all"]),
        ["asm-package"] = new("tests/projects/NetAsmPackage/NetAsmPackage.csproj", "NetAsmPackage", "NetAsmPackageReference", []),
        ["embedding-package"] = new("tests/projects/NetLayoutPackage/NetLayoutPackage.csproj", "NetLayoutPackage", "NetLayoutPackageReference", []),
        // The PackageReference is to the wrapper, which only depends on the asm package: the
        // layout comes from the property, which the asm package's own targets read.
        ["dependency-package"] = new("tests/projects/NetLayoutPackage/NetLayoutPackage.csproj", "NetLayoutPackage", "BinariesCopyPath", ["-p:NetLayoutPackageVersion=2.0.0"]),
    };

    /// <summary>The test projects the cases build, whose bin/ and obj/ a preparation removes.</summary>
    private static readonly string[] Projects = ["Everywhere", "EverywhereStatic", "NetLayout", "NetLayoutLibrary", "NetLayoutThrough", "NetLayoutPackage", "NetAsmPackage"];

    /// <summary>The repository root.</summary>
    private readonly string _root;

    /// <summary>llvm-readobj of this machine's staged toolchain.</summary>
    private readonly string _readObj;

    /// <summary>The environment changes for every child process (null removes a variable).</summary>
    private readonly Dictionary<string, string?> _environment = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the toolchain, Docker, and sets up the child processes' environment.</summary>
    /// <param name="root">The repository root.</param>
    /// <exception cref="MatrixException">This machine's toolchain is not staged, or no toolchain exists for this machine.</exception>
    public Matrix(string root)
    {
        _root = root;
        Output = Path.Combine(root, "artifacts", "qa", "delivery");
        HostRid = ComputeHostRid();
        string tools = Path.Combine(root, "artifacts", "toolchain", HostRid, "tools");
        if (!File.Exists(Path.Combine(tools, "VERSIONS.txt")))
        {
            throw new MatrixException($"No staged toolchain for {HostRid}. Run: dotnet run toolchain/fetch.cs -- --rid {HostRid}");
        }

        _readObj = Path.Combine(tools, OperatingSystem.IsWindows() ? "llvm-readobj.exe" : "llvm-readobj");
        if (!OperatingSystem.IsWindows())
        {
            // CI artifacts and NuGet drop the execute bit, and llvm-readobj is called directly.
            File.SetUnixFileMode(_readObj, File.GetUnixFileMode(_readObj) | UnixFileMode.UserExecute);
        }

        // As tests/AsmSdk.Tests does (SdkUnderTest): a NuGet cache of its own, so the SDK packed
        // from this checkout is used even though its version equals a released one; no
        // MSBuild variables of the dotnet that runs this app leak into the builds.
        foreach (string name in Environment.GetEnvironmentVariables().Keys.OfType<string>()
                     .Where(n => n.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)))
        {
            _environment[name] = null;
        }

        _environment["NUGET_PACKAGES"] = Path.Combine(root, "artifacts", "qa-nuget");
        _environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        _environment["DOTNET_NOLOGO"] = "1";

        // NativeAOT finds Visual Studio's linker by running vswhere.exe from the Installer
        // folder, which Git Bash's environment can lack (measured 2026-11-20, see SdkUnderTest).
        if (OperatingSystem.IsWindows())
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string installer = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer");
            if (programFilesX86.Length != 0 && File.Exists(Path.Combine(installer, "vswhere.exe")))
            {
                _environment["ProgramFiles(x86)"] = programFilesX86;
                _environment["PATH"] = installer + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            }
        }

        HasDocker = Run("docker", ["info", "--format", "{{.OSType}}"], root, 60) is (0, string os) && os.Trim() == "linux";
        Runnable = Rids.Where(CanRun).ToArray();
    }

    /// <summary>The folder of the outputs and the report: artifacts/qa/delivery.</summary>
    public string Output { get; }

    /// <summary>This machine's RID (that of its toolchain package).</summary>
    public string HostRid { get; }

    /// <summary>Whether Docker runs Linux containers here.</summary>
    public bool HasDocker { get; }

    /// <summary>The RIDs whose programs this machine runs: its own, and the Linux ones when Docker is there.</summary>
    public string[] Runnable { get; }

    /// <summary>The matrix for this machine (see the file header for its parts).</summary>
    /// <returns>The cases, in the order they run.</returns>
    public IEnumerable<Case> Cases()
    {
        // Portable builds: every path, every layout that separates RIDs, run on every runnable RID.
        foreach (string path in Paths.Keys)
        {
            foreach (string layout in new[] { "", "native/{rid}", "RidFolders" })
            {
                yield return new Case($"build-{path}-portable-{Slug(layout)}", path, "build", null, layout, [], Runnable);
            }
        }

        // Builds for each RID: the direct reference and both package kinds, the default layout
        // (beside the app) and a flat folder (fine for one RID), run where the RID runs.
        foreach (string path in new[] { "project", "asm-package", "embedding-package" })
        {
            foreach (string rid in Rids)
            {
                foreach (string layout in new[] { "", "native" })
                {
                    yield return new Case($"build-{path}-{rid}-{Slug(layout)}", path, "build", rid, layout, [], CanRun(rid) ? [rid] : []);
                }
            }
        }

        // Self-contained publishes, a per-RID folder, for every runnable RID.
        foreach (string path in new[] { "project", "through-library", "private-library", "embedding-package" })
        {
            foreach (string rid in Runnable)
            {
                yield return new Case($"publish-sc-{path}-{rid}", path, "publish", rid, "native/{rid}", ["--self-contained"], [rid], SelfContained: true, Executable: true);
            }
        }

        // Single file, for this machine and Linux x64: the direct reference, and the private
        // library, whose program cannot mark the files AssetType=native (NETSDK1236 otherwise).
        foreach (string rid in new[] { HostRid, "linux-x64" }.Distinct().Where(CanRun))
        {
            yield return new Case($"publish-single-file-project-{rid}", "project", "publish", rid, "native", ["--self-contained", "-p:PublishSingleFile=true"], [rid], SelfContained: true, Executable: true);
            yield return new Case($"publish-single-file-private-library-{rid}", "private-library", "publish", rid, "native/{rid}", ["--self-contained", "-p:PublishSingleFile=true"], [rid], SelfContained: true, Executable: true);
        }

        // NativeAOT compiles for this machine only (no cross-OS NativeAOT): three paths, and the
        // negative control without the generated loader.
        foreach (string path in new[] { "project", "embedding-package", "asm-package" })
        {
            yield return new Case($"publish-aot-{path}-{HostRid}", path, "publish", HostRid, "native", ["-p:PublishAot=true"], [HostRid], SelfContained: true, Executable: true);
        }

        yield return new Case($"publish-aot-project-{HostRid}-loader-off", "project", "publish", HostRid, "native",
            ["-p:PublishAot=true", "-p:AsmGenerateNativeLibraryLoader=false"], [HostRid], SelfContained: true, Executable: true, ExpectRunFailure: true);
    }

    /// <summary>
    /// Packs everything the cases consume into artifacts/packages, after removing what a
    /// previous run left: the SDK and this machine's toolchain from this checkout, the asm
    /// package Test.Everywhere, and the two wrapper packages; then checks the packages'
    /// dependencies, which decide what the package paths prove.
    /// </summary>
    /// <exception cref="MatrixException">A pack failed, or a package has the wrong dependencies.</exception>
    public void Prepare()
    {
        DeleteDirectory(Output);
        DeleteDirectory(Path.Combine(_root, "artifacts", "qa-nuget"));
        foreach (string project in Projects)
        {
            DeleteDirectory(Path.Combine(_root, "tests", "projects", project, "bin"));
            DeleteDirectory(Path.Combine(_root, "tests", "projects", project, "obj"));
        }

        string feed = Path.Combine(_root, "artifacts", "packages");
        Directory.CreateDirectory(feed);
        foreach (string stale in Directory.GetFiles(feed, "Test.*.nupkg"))
        {
            File.Delete(stale);
        }

        Step("pack AsmFoundation.Sdk", "pack", "src/AsmFoundation.Sdk/AsmFoundation.Sdk.csproj", "-c", "Release");
        Step($"pack AsmFoundation.Toolchain.{HostRid}", "pack", "toolchain/AsmFoundation.Toolchain.csproj", "-c", "Release", $"-p:ToolchainRid={HostRid}");
        // Its static library linked in, not a dependency: everywherestatic names no RID and is
        // never packed, so a package depending on it could not be restored (NU1101).
        Step("pack Test.Everywhere 1.0.0", "pack", "tests/projects/Everywhere/Everywhere.asmproj", "-c", "Release", "-p:EverywhereStaticPrivate=all");
        Step("pack Test.NetLayoutLibrary 1.0.0 (binaries inside)", "pack", "tests/projects/NetLayoutLibrary/NetLayoutLibrary.csproj", "-c", "Release", "-p:NetLayoutLibraryPrivate=all");
        // The wrapper's restore state names the feed as it was when it was packed; the
        // consumers restore their own, so none of it may linger into the next pack or a case.
        DeleteDirectory(Path.Combine(_root, "tests", "projects", "NetLayoutLibrary", "obj"));
        Step("pack Test.NetLayoutLibrary 2.0.0 (depends on Test.Everywhere)", "pack", "tests/projects/NetLayoutLibrary/NetLayoutLibrary.csproj", "-c", "Release", "-p:NetLayoutLibraryVersion=2.0.0");
        DeleteDirectory(Path.Combine(_root, "tests", "projects", "NetLayoutLibrary", "obj"));

        CheckDependencies(feed, "Test.Everywhere.1.0.0.nupkg", null, "everywherestatic");
        CheckDependencies(feed, "Test.NetLayoutLibrary.1.0.0.nupkg", null, "Test.Everywhere");
        CheckDependencies(feed, "Test.NetLayoutLibrary.2.0.0.nupkg", "id=\"Test.Everywhere\" version=\"1.0.0\"", "everywherestatic");
    }

    /// <summary>Runs one case: build or publish, then files, formats, deps.json and runs.</summary>
    /// <param name="c">The case.</param>
    /// <returns>What it found.</returns>
    public Result Check(Case c)
    {
        DeliveryPath path = Paths[c.Path];
        string output = Path.Combine(Output, c.Name);
        DeleteDirectory(output);
        var arguments = new List<string> { c.Verb, path.Project, "-c", "Release", "-nodeReuse:false", "-v:q" };
        // dotnet build -o is every project's output folder: through a wrapper library, the
        // wrapper's own copies would land beside the program's (a documented footgun), so the
        // program alone gets the folder (NetLayoutThrough's OutDir). dotnet publish -o has no
        // such effect.
        if (c.Verb == "build" && c.Path is "through-library" or "private-library")
        {
            arguments.Add($"-p:NetLayoutThroughOutput={output}{Path.DirectorySeparatorChar}");
        }
        else
        {
            arguments.AddRange(["-o", output]);
        }

        arguments.AddRange(path.Extra);
        arguments.AddRange(c.Extra);
        if (c.Rid is not null)
        {
            arguments.AddRange(["-r", c.Rid]);
        }

        if (c.Layout.Length != 0)
        {
            arguments.Add($"-p:{path.LayoutProperty}={c.Layout}");
        }

        var watch = Stopwatch.StartNew();
        (int code, string log) = Run("dotnet", arguments, _root);
        var result = new Result(c, (int)watch.Elapsed.TotalSeconds);
        if (code != 0)
        {
            if (c.Extra.Contains("-p:PublishAot=true") && (log.Contains("Platform linker", StringComparison.Ordinal) || log.Contains("nativeaot-prerequisites", StringComparison.Ordinal)))
            {
                return result.Skip("NativeAOT's platform linker is missing (https://aka.ms/nativeaot-prerequisites)");
            }

            IEnumerable<string> errors = log.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Distinct().Take(3);
            return result.Fail($"{c.Verb} exited {code}: {string.Join("; ", errors)}");
        }

        // A private library's program has no AsmSdk targets to keep one RID: it copies every
        // RID the RID-agnostic library hands on, laid out the way the library's portable build
        // lays them out, whatever the program's own RID.
        bool everyRid = c.Rid is null || c.Path == "private-library";
        string folder = FolderFor(c.Layout, ridSpecific: !everyRid);
        string[] targets = everyRid ? Rids : [c.Rid!];
        List<string> expected = targets.Select(rid => Destination(folder, rid)).Order(StringComparer.Ordinal).ToList();
        result.Files.AddRange(EverywhereFiles(output));
        if (!result.Files.SequenceEqual(expected))
        {
            result.Fail($"files: expected [{string.Join(", ", expected)}], found [{string.Join(", ", result.Files)}]");
        }

        foreach (string rid in targets)
        {
            string file = Path.Combine(output, Destination(folder, rid));
            if (File.Exists(file))
            {
                string format = FileFormat(file);
                result.Formats[rid] = format;
                if (format != Formats[rid])
                {
                    result.Fail($"format: {rid} is {format}, expected {Formats[rid]}");
                }
            }
        }

        string depsFile = Path.Combine(output, path.Assembly + ".deps.json");
        if (c.Path != "private-library" && File.Exists(depsFile))
        {
            foreach (KeyValuePair<string, string> entry in DepsLocalPaths(depsFile))
            {
                result.Deps[entry.Key] = entry.Value;
            }

            List<string> missing = expected.Where(p => !result.Deps.ContainsValue(p)).ToList();
            if (missing.Count != 0)
            {
                result.Fail($"deps.json: no localPath for [{string.Join(", ", missing)}]");
            }
        }

        foreach (string rid in c.RunOn)
        {
            (bool? passed, string line) = RunProgram(output, path.Assembly, rid, c.SelfContained, c.Executable);
            result.Runs[rid] = line;
            if (c.ExpectRunFailure)
            {
                if (passed is not false || !line.Contains("DllNotFoundException", StringComparison.Ordinal))
                {
                    result.Fail($"negative control: the run on {rid} did not fail with DllNotFoundException: {line}");
                }
            }
            else if (passed is false)
            {
                result.Fail($"run {rid}: {line}");
            }
        }

        return result;
    }

    /// <summary>
    /// Writes artifacts/qa/delivery/results.json (every finding) and results.md (the summary:
    /// cases per group and path, binaries and formats checked, runs per RID, skips, failures).
    /// </summary>
    /// <param name="results">The results so far.</param>
    public void WriteReport(IReadOnlyList<Result> results)
    {
        Directory.CreateDirectory(Output);
        var json = new JsonArray(results.Select(r => (JsonNode?)r.ToJson()).ToArray());
        File.WriteAllText(Path.Combine(Output, "results.json"), json.ToJsonString(new() { WriteIndented = true }));

        var md = new StringBuilder();
        int failed = results.Count(r => r.Status == Status.Fail);
        int skipped = results.Count(r => r.Status == Status.Skip);
        md.Append($"# Delivery QA matrix\n\n{results.Count - failed - skipped}/{results.Count} passed, {skipped} skipped, {failed} failed, on {HostRid}")
          .Append($" (runnable: {string.Join(", ", Runnable)}).\n\n")
          .Append("| Kind | Path | Passed | Skipped | Failed | Binaries checked | Formats read |\n|---|---|---|---|---|---|---|\n");
        foreach (IGrouping<(string Kind, string Path), Result> group in results.GroupBy(r => (Kind(r.Case), r.Case.Path)))
        {
            md.Append($"| {group.Key.Kind} | {group.Key.Path} | {group.Count(r => r.Status == Status.Pass)} | {group.Count(r => r.Status == Status.Skip)} | {group.Count(r => r.Status == Status.Fail)} | {group.Sum(r => r.Files.Count)} | {group.Sum(r => r.Formats.Count)} |\n");
        }

        IEnumerable<string> runs = results.SelectMany(r => r.Runs.Keys).GroupBy(rid => rid).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}");
        md.Append($"\nProgram runs: {results.Sum(r => r.Runs.Count)} ({string.Join(", ", runs)}).\n");
        foreach (Result skip in results.Where(r => r.Status == Status.Skip))
        {
            md.Append($"\n- skipped `{skip.Case.Name}`: {skip.SkipReason}");
        }

        foreach (Result failure in results.Where(r => r.Status == Status.Fail))
        {
            md.Append($"\n- **failed** `{failure.Case.Name}`: {string.Join(" | ", failure.Problems)}");
        }

        File.WriteAllText(Path.Combine(Output, "results.md"), md.Append('\n').ToString());
    }

    /// <summary>The report's group of a case: build, publish-sc, publish-single or publish-aot.</summary>
    /// <param name="c">The case.</param>
    /// <returns>The group's name.</returns>
    private static string Kind(Case c) =>
        c.Verb == "build" ? "build" : string.Join('-', c.Name.Split('-').Take(2));

    /// <summary>Whether this machine runs a RID's programs: its own, or a Linux one in a container.</summary>
    /// <param name="rid">The RID.</param>
    /// <returns>True when a case for it can be run here.</returns>
    private bool CanRun(string rid) => rid == HostRid || (HasDocker && Containers.ContainsKey(rid));

    /// <summary>Runs a program of an output where this machine runs its RID.</summary>
    /// <param name="output">The output or publish folder.</param>
    /// <param name="assembly">The program's assembly name.</param>
    /// <param name="rid">The RID it runs as.</param>
    /// <param name="selfContained">Whether it needs no .NET runtime (a smaller container image).</param>
    /// <param name="executable">Whether it starts as its own executable (else with dotnet).</param>
    /// <returns>Whether it printed its RID's line (null when it cannot run here), and its first line.</returns>
    private (bool? Passed, string Line) RunProgram(string output, string assembly, string rid, bool selfContained, bool executable)
    {
        string expected = $"{rid} 42 {PlatformCode[rid]} ";
        (int Code, string Text) run;
        if (rid == HostRid)
        {
            if (executable)
            {
                string program = Path.Combine(output, OperatingSystem.IsWindows() ? assembly + ".exe" : assembly);
                if (!OperatingSystem.IsWindows() && File.Exists(program))
                {
                    File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
                }

                run = Run(program, [], output, 600);
            }
            else
            {
                run = Run("dotnet", [Path.Combine(output, assembly + ".dll")], output, 600);
            }
        }
        else if (HasDocker && Containers.TryGetValue(rid, out LinuxImages? images))
        {
            // Windows file systems, NuGet and CI artifacts drop the execute bit: set it inside.
            string inner = executable ? $"chmod +x /app/{assembly} && /app/{assembly}" : $"dotnet /app/{assembly}.dll";
            run = Run("docker", ["run", "--rm", "--platform", images.Platform, "-v", $"{output}:/app",
                selfContained ? images.SelfContained : images.Dependent, "sh", "-c", inner], _root, 600);
        }
        else
        {
            return (null, "not runnable here");
        }

        string text = run.Text.Trim();
        string line = text.Length == 0 ? $"exit {run.Code}" : text.Split('\n')[0].Trim();
        return (run.Code == 0 && line.StartsWith(expected, StringComparison.Ordinal), line.Length > 110 ? line[..110] : line);
    }

    /// <summary>The object format and CPU of a binary, as llvm-readobj names them.</summary>
    /// <param name="file">The binary.</param>
    /// <returns>The "Format:" value ("COFF-x86-64", "elf64-littleaarch64", ...), or "unreadable (exit N)".</returns>
    private string FileFormat(string file)
    {
        (int code, string text) = Run(_readObj, ["--file-headers", file], _root, 120);
        string? format = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Format: ", StringComparison.Ordinal));
        return format is null ? $"unreadable (exit {code})" : format["Format: ".Length..].Trim();
    }

    /// <summary>Every asset of deps.json that names Everywhere's library, with its localPath.</summary>
    /// <param name="depsFile">The program's deps.json.</param>
    /// <returns>"library:asset" to localPath ("" when the asset has none).</returns>
    /// <exception cref="System.Text.Json.JsonException">The file is not JSON.</exception>
    private static Dictionary<string, string> DepsLocalPaths(string depsFile)
    {
        JsonNode deps = JsonNode.Parse(File.ReadAllText(depsFile))!;
        string target = deps["runtimeTarget"]!["name"]!.GetValue<string>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> library in deps["targets"]![target]!.AsObject())
        {
            foreach (string kind in new[] { "runtimeTargets", "native" })
            {
                if (library.Value?[kind] is not JsonObject assets)
                {
                    continue;
                }

                foreach (KeyValuePair<string, JsonNode?> asset in assets.Where(a => a.Key.Contains("everywhere", StringComparison.OrdinalIgnoreCase)))
                {
                    paths[$"{library.Key}:{asset.Key}"] = asset.Value?["localPath"]?.GetValue<string>() ?? "";
                }
            }
        }

        return paths;
    }

    /// <summary>Every build of Everywhere's library in a folder, relative and with '/', sorted.</summary>
    /// <param name="output">The folder.</param>
    /// <returns>The paths; empty when the folder does not exist.</returns>
    private static List<string> EverywhereFiles(string output) =>
        !Directory.Exists(output) ? [] :
        Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileNameWithoutExtension(f) is "everywhere" or "libeverywhere" && Path.GetExtension(f) is ".dll" or ".so" or ".dylib")
            .Select(f => Path.GetRelativePath(output, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>The folder a layout means ("" = the app folder; {rid} = each file's RID).</summary>
    /// <param name="layout">The BinariesCopyPath value of a case ("" = Auto).</param>
    /// <param name="ridSpecific">Whether the program is built for one RID (Auto then means the app folder).</param>
    /// <returns>The folder pattern.</returns>
    /// <exception cref="ArgumentException">A layout the matrix does not use.</exception>
    private static string FolderFor(string layout, bool ridSpecific) => layout switch
    {
        "" => ridSpecific ? "" : "runtimes/{rid}/native",
        "native/{rid}" => "native/{rid}",
        "RidFolders" => "{rid}",
        "native" => "native",
        "AppFolder" => "",
        _ => throw new ArgumentException($"no expectation for the layout '{layout}'", nameof(layout)),
    };

    /// <summary>Where a layout puts one RID's library, relative to the output.</summary>
    /// <param name="folder">The folder pattern.</param>
    /// <param name="rid">The RID.</param>
    /// <returns>For example "native/linux-x64/libeverywhere.so".</returns>
    private static string Destination(string folder, string rid) =>
        (folder.Length == 0 ? "" : folder.Replace("{rid}", rid) + "/")
        + (rid.StartsWith("win-", StringComparison.Ordinal) ? "everywhere.dll" : rid.StartsWith("osx-", StringComparison.Ordinal) ? "libeverywhere.dylib" : "libeverywhere.so");

    /// <summary>A layout as part of a case name: "default", "native_rid", "RidFolders", "native".</summary>
    /// <param name="layout">The layout ("" = default).</param>
    /// <returns>The slug.</returns>
    private static string Slug(string layout) => layout.Length == 0 ? "default" : layout.Replace("/", "_").Replace("{rid}", "rid");

    /// <summary>Checks a packed package's dependencies against what its delivery path needs.</summary>
    /// <param name="feed">The package folder.</param>
    /// <param name="package">The .nupkg file name.</param>
    /// <param name="mustContain">Text the .nuspec must contain, or null.</param>
    /// <param name="mustNotContain">Text the .nuspec must not contain.</param>
    /// <exception cref="MatrixException">The package is missing or its dependencies are wrong.</exception>
    private static void CheckDependencies(string feed, string package, string? mustContain, string mustNotContain)
    {
        string file = Path.Combine(feed, package);
        if (!File.Exists(file))
        {
            throw new MatrixException($"{package} was not packed");
        }

        using ZipArchive archive = ZipFile.OpenRead(file);
        ZipArchiveEntry entry = archive.Entries.First(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open());
        string nuspec = reader.ReadToEnd();
        if ((mustContain is not null && !nuspec.Contains(mustContain, StringComparison.Ordinal)) || nuspec.Contains(mustNotContain, StringComparison.Ordinal))
        {
            throw new MatrixException($"{package} has the wrong dependencies (needs {mustContain ?? "-"}, must not name {mustNotContain}):\n{nuspec}");
        }

        Console.WriteLine($"  {package}: dependencies as expected");
    }

    /// <summary>Runs one dotnet preparation step.</summary>
    /// <param name="what">What it does, for the console and the error.</param>
    /// <param name="arguments">The arguments after dotnet, paths relative to the repository root.</param>
    /// <exception cref="MatrixException">The step failed; the message ends with its output's tail.</exception>
    private void Step(string what, params string[] arguments)
    {
        (int code, string log) = Run("dotnet", [.. arguments, "-nodeReuse:false", "-v:q"], _root);
        Console.WriteLine($"  {what}: exit {code}");
        if (code != 0)
        {
            throw new MatrixException($"{what} failed:\n{(log.Length > 3000 ? log[^3000..] : log)}");
        }
    }

    /// <summary>
    /// Runs a process with the matrix's environment, and returns its exit code and output
    /// (standard output, then standard error).
    /// </summary>
    /// <param name="file">The program.</param>
    /// <param name="arguments">Its arguments, each passed as one argument (no shell).</param>
    /// <param name="workingDirectory">Where it runs.</param>
    /// <param name="timeoutSeconds">How long it may take; then it is killed and the code is 124.</param>
    /// <returns>The exit code (-1 when it could not start) and the output.</returns>
    private (int Code, string Text) Run(string file, IEnumerable<string> arguments, string workingDirectory, int timeoutSeconds = 1800)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string?> variable in _environment)
        {
            if (variable.Value is null)
            {
                start.Environment.Remove(variable.Key);
            }
            else
            {
                start.Environment[variable.Key] = variable.Value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // A program that is not there (docker on a machine without it) is an answer, not a crash.
            return (-1, $"cannot start {file}: {e.Message}");
        }

        using (process)
        {
            // Both streams are read as they come: a child that fills one pipe while the other
            // is waited on would block forever.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.Append(e.Data).Append('\n'); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.Append(e.Data).Append('\n'); } } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                process.Kill(entireProcessTree: true);
                return (124, $"timed out after {timeoutSeconds} s: {file} {string.Join(' ', start.ArgumentList)}");
            }

            // The parameterless wait also waits for the asynchronous readers to drain.
            process.WaitForExit();
            return (process.ExitCode, stdout.ToString() + stderr);
        }
    }

    /// <summary>Removes a folder and everything in it, when it exists; retries while Windows still holds a file.</summary>
    /// <param name="folder">The folder.</param>
    /// <exception cref="IOException">The folder is still in use after the retries.</exception>
    private static void DeleteDirectory(string folder)
    {
        for (int attempt = 1; Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
            {
                // A build server or an antivirus scan that just closed a file releases it shortly.
                Thread.Sleep(500 * attempt);
            }
        }
    }

    /// <summary>This machine's RID, in the toolchain packages' naming.</summary>
    /// <returns>win, linux or osx, with x64 or arm64.</returns>
    /// <exception cref="MatrixException">An OS or CPU no toolchain package exists for.</exception>
    private static string ComputeHostRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : throw new MatrixException($"No toolchain package for {RuntimeInformation.OSDescription}");
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture other => throw new MatrixException($"No toolchain package for {other}"),
        };
        return $"{os}-{arch}";
    }
}
