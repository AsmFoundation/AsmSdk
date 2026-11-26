using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The realistic samples, the ones the documentation's Examples section walks through: Crc32C (an
/// assembly library for .NET, with its packages, its own unit tests, its benchmark's self-check and
/// a NuGet consumer), WordCount (a libc tool) and HexDump (a freestanding tool). Each fact the
/// Examples pages state about them is checked here.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class ExampleTests(SdkUnderTest sdk)
{
    /// <summary>
    /// An MSBuild or NuGet warning line; dotnet pack prints no warning summary at its default
    /// verbosity, so pack checks look for the lines themselves.
    /// </summary>
    private static readonly Regex MsBuildWarning = new("warning [A-Z]+[0-9]+");

    /// <summary>libcrc32c.so, Crc32C's freestanding shared library.</summary>
    private static readonly string Crc32CLibrary = Repo.Sample("Crc32C", "bin", "Release", "freestanding", "linux-x64", "libcrc32c.so");

    /// <summary>WordCount's executable (libc).</summary>
    private static readonly string WordCount = Repo.Sample("WordCount", "bin", "Release", "libc", "linux-x64", "wordcount");

    /// <summary>HexDump's executable (freestanding).</summary>
    private static readonly string HexDump = Repo.Sample("HexDump", "bin", "Release", "freestanding", "linux-x64", "hexdump");

    /// <summary>Where the tool runs keep their input files: under the repository, so WSL sees them at the same relative path.</summary>
    private static readonly string RunFolder = Path.Combine(Repo.Artifacts, "test-run");

    /// <summary>Fails the calling test at once, with the build log, when the samples did not build.</summary>
    private void RequireSamples() =>
        Assert.True(sdk.SamplesBuild.ExitCode == 0, "the samples did not build: " + sdk.SamplesBuild.Tail());

    /// <summary>Packs Crc32C (the asm package) and Crc32C.Net (the managed one) into the local feed, once each.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The two packs' results, asm first.</returns>
    internal static (CommandResult Native, CommandResult Managed) PackCrc32C(SdkUnderTest sdk) =>
        (sdk.Once("pack Crc32C", () => sdk.Dotnet("pack", "samples/Crc32C/Crc32C.asmproj", "-c", "Release")),
         sdk.Once("pack Crc32C.Net", () => sdk.Dotnet("pack", "samples/Crc32C.Net/Crc32C.Net.csproj", "-c", "Release")));

    /// <summary>Builds samples/consumers/Crc32C.App against the two packages, by version, once.</summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The build's result (after packing, which it needs).</returns>
    internal static CommandResult BuildCrc32CApp(SdkUnderTest sdk)
    {
        PackCrc32C(sdk);
        return sdk.Once("build Crc32C.App",
            () => sdk.Dotnet("build", "samples/consumers/Crc32C.App/Crc32C.App.csproj", "-c", "Release"));
    }

    // ---- Crc32C: the library ----

    /// <summary>
    /// ExportsFile in a real library: exactly the five names of crc32c.map leave libcrc32c.so,
    /// each versioned CRC32C_1.0; it needs no other library and has no text relocation.
    /// </summary>
    [Fact]
    public void Crc32C_exports_exactly_its_versioned_names()
    {
        RequireSamples();
        string symbols = sdk.ReadElf(Crc32CLibrary, "--dyn-syms");
        // A version name holds dots (CRC32C_1.0), which \w does not match.
        string[] exported = Regex.Matches(symbols, @"\b(crc32c_\w+)@@([\w.]+)").Select(m => m.Value)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["crc32c_append@@CRC32C_1.0", "crc32c_append_hardware@@CRC32C_1.0", "crc32c_append_hardware_3way@@CRC32C_1.0",
             "crc32c_append_portable@@CRC32C_1.0", "crc32c_implementation@@CRC32C_1.0"],
            exported);
        Assert.DoesNotContain("select", symbols);           // a local label of the dispatcher
        string dynamic = sdk.ReadElf(Crc32CLibrary, "-d");
        Assert.DoesNotContain("(NEEDED)", dynamic);
        Assert.DoesNotContain("TEXTREL", dynamic);
    }

    /// <summary>
    /// The constants NASM's preprocessor computed are the expected ones: the merge multipliers of
    /// the 3-way kernel (x^(8n-33) mod P for 1,024/512 and 128/64 bytes) and the CRC table's first
    /// entries, as the Examples page shows them.
    /// </summary>
    [Fact]
    public void Crc32C_constants_were_computed_at_assembly_time()
    {
        RequireSamples();
        string dump = sdk.ReadElf(Crc32CLibrary, "--hex-dump=.rodata");
        // llvm-readobj prints little-endian words as bytes: 0x170076FA appears as "fa760017".
        foreach (string word in new[] { "fa760017", "0c3b7edd", "92603b0d", "f8dd4a9e", "03836bf2", "f7703be1" })
        {
            Assert.Contains(word, dump);
        }
    }

    // ---- Crc32C: the packages and the consumer ----

    /// <summary>
    /// The asm package for .NET: each of the seven native RIDs' libraries under
    /// runtimes/&lt;rid&gt;/native/ (where .NET finds a RID-specific native asset), the import
    /// libraries of the Windows DLLs under native/ (for asm consumers), each WebAssembly RID's
    /// archive (what a .NET WebAssembly app links, through the props) and module (for JavaScript
    /// and WASI hosts) under native/ and never runtimes/, the public include file,
    /// netstandard1.0's lib/ marker and props, and the .NET consumer targets that give a
    /// PackageReference BinariesCopyPath (a shared library is packed, so they are there); and, the
    /// library having PROTO lines, its C# bindings: the baseline its C# consumers compile
    /// (contentFiles), the API file and the bindings generator (build/bindings/); and its C header
    /// (include/crc32c.h), from the same lines.
    /// </summary>
    [Fact]
    public void Crc32C_packs_its_library_where_dotnet_finds_it()
    {
        (CommandResult pack, _) = PackCrc32C(sdk);
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                "Samples.Crc32C.nuspec", "include/crc32c.inc", "lib/netstandard1.0/_._",
                "build/netstandard1.0/Samples.Crc32C.props", "buildTransitive/netstandard1.0/Samples.Crc32C.props",
                "build/netstandard1.0/Samples.Crc32C.targets", "buildTransitive/netstandard1.0/Samples.Crc32C.targets",
                "runtimes/win-x64/native/crc32c.dll", "runtimes/win-arm64/native/crc32c.dll",
                "runtimes/linux-x64/native/libcrc32c.so", "runtimes/linux-arm64/native/libcrc32c.so",
                "runtimes/linux-musl-x64/native/libcrc32c.so",
                "runtimes/osx-x64/native/libcrc32c.dylib", "runtimes/osx-arm64/native/libcrc32c.dylib",
                "native/win-x64/freestanding/crc32c.dll.lib", "native/win-arm64/freestanding/crc32c.dll.lib",
                "native/browser-wasm/freestanding/crc32c.a", "native/browser-wasm/freestanding/crc32c.wasm",
                "native/wasi-wasm/freestanding/crc32c.a", "native/wasi-wasm/freestanding/crc32c.wasm",
                "include/crc32c.h", "contentFiles/cs/net7.0/AsmBindings/crc32c.cs.pp",
                "build/bindings/crc32c.api.json", "build/bindings/AsmFoundation.Sdk.Bindings.dll",
            },
            SdkUnderTest.PackageEntries(Path.Combine(Repo.Artifacts, "packages", "Samples.Crc32C.1.0.0.nupkg")));
    }

    /// <summary>
    /// The managed package holds only the C# assembly (and its XML docs), and depends on the asm
    /// package by version: pack turned the ProjectReference to the .asmproj into that dependency.
    /// </summary>
    [Fact]
    public void Crc32C_Net_packs_a_dependency_on_the_asm_package()
    {
        (_, CommandResult pack) = PackCrc32C(sdk);
        Assert.True(pack.ExitCode == 0 && !MsBuildWarning.IsMatch(pack.StdOut), pack.Tail());
        string nupkg = Path.Combine(Repo.Artifacts, "packages", "Samples.Crc32C.Net.1.0.0.nupkg");
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                "Samples.Crc32C.Net.nuspec", "lib/net10.0/Samples.Crc32C.Net.dll", "lib/net10.0/Samples.Crc32C.Net.xml",
            },
            SdkUnderTest.PackageEntries(nupkg));
        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        using var reader = new StreamReader(archive.GetEntry("Samples.Crc32C.Net.nuspec")!.Open());
        Assert.Matches(new Regex(@"<dependency id=""Samples\.Crc32C"" version=""1\.0\.0"""), reader.ReadToEnd());
    }

    /// <summary>
    /// A .NET program that references only Samples.Crc32C.Net by version receives libcrc32c
    /// through the dependency, every platform's build in its output's runtimes/&lt;rid&gt;/native/.
    /// </summary>
    [Fact]
    public void A_dotnet_program_gets_the_asm_library_through_the_wrapper_package()
    {
        CommandResult build = BuildCrc32CApp(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.True(File.Exists(Repo.Sample("consumers", "Crc32C.App", "bin", "Release", "net10.0",
            "runtimes", "linux-x64", "native", "libcrc32c.so")));
        Assert.True(File.Exists(Repo.Sample("consumers", "Crc32C.App", "bin", "Release", "net10.0",
            "runtimes", "win-arm64", "native", "crc32c.dll")));
    }

    // ---- Crc32C: running it ----

    /// <summary>
    /// The library's own xunit tests (Crc32C.Tests) pass on this machine, whichever of the seven
    /// platforms it is: every path against a C# reference, with this platform's build of the
    /// library (the portable output carries all seven).
    /// </summary>
    [Fact]
    public void Crc32C_unit_tests_pass()
    {
        RequireSamples();
        CommandResult run = Command.Run("dotnet", [Repo.Sample("Crc32C.Tests", "bin", "Release", "net10.0", "Crc32C.Tests.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0 && run.StdOut.Contains("Failed: 0", StringComparison.Ordinal), $"exit {run.ExitCode}: {run.Output}");
    }

    /// <summary>
    /// The benchmark's self-check, on this machine: every path agrees with BitOperations.Crc32C at
    /// every benchmark size.
    /// </summary>
    [Fact]
    public void Crc32C_benchmark_check_passes()
    {
        RequireSamples();
        CommandResult run = Command.Run("dotnet", [Repo.Sample("Crc32C.Bench", "bin", "Release", "net10.0", "Crc32C.Bench.dll"), "--check"], Repo.Root);
        Assert.True(run.ExitCode == 0 && run.StdOut.StartsWith("OK:", StringComparison.Ordinal), $"exit {run.ExitCode}: {run.Output}");
    }

    /// <summary>
    /// The NuGet consumer runs on this machine: the check value, computed in assembly that came
    /// from the packages (NuGet's runtimes/ resolution picked this platform's build).
    /// </summary>
    [Fact]
    public void Crc32C_App_computes_the_check_value_from_the_packages()
    {
        CommandResult build = BuildCrc32CApp(sdk);
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.Sample("consumers", "Crc32C.App", "bin", "Release", "net10.0", "crc32c-app.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0 && run.StdOut.Contains("crc32c(\"123456789\") = e3069283", StringComparison.Ordinal),
            $"exit {run.ExitCode}: {run.Output}");
    }

    // ---- WordCount ----

    /// <summary>
    /// WordCount is a libc PIE that imports the functions it calls, warn and warnx among them
    /// (added to the stub by AsmLibcSymbol), and stderr, a libc data object.
    /// </summary>
    [Fact]
    public void WordCount_imports_libc_functions_and_data()
    {
        RequireSamples();
        string text = sdk.ReadElf(WordCount, "-d", "--dyn-syms");
        Assert.Contains("Shared library: [libc.so.6]", text);
        foreach (string name in new[] { "open", "read", "close", "printf", "fputs", "warn", "warnx" })
        {
            Assert.Matches(new Regex($@"FUNC\s+GLOBAL\s+DEFAULT\s+UND\s+{name}\b"), text);
        }

        Assert.Matches(new Regex(@"OBJECT\s+GLOBAL\s+DEFAULT\s+\S+\s+stderr\b"), text);
    }

    /// <summary>
    /// WordCount's counts, against a C# count of the same files: a short text, an empty file, and
    /// 200,000 bytes that hold one word spanning the 64 KiB read boundary and lines of mixed
    /// whitespace; plus the total line, the -l/-w/-c selection and standard input.
    /// </summary>
    [Fact]
    public void WordCount_counts_like_wc()
    {
        RequireSamples();
        string folder = Directory.CreateDirectory(Path.Combine(RunFolder, "wordcount")).FullName;
        // An array, not a dictionary: the order is the command line's, and so the output's.
        (string Name, byte[] Content)[] files =
        [
            ("text.txt", "hello world\nfoo  bar\tbaz\n\n x"u8.ToArray()),
            ("empty.txt", []),
            ("long.txt", LongText()),
        ];
        foreach ((string name, byte[] content) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, name), content);
        }

        string exe = Repo.Relative(WordCount);
        string dir = Repo.Relative(folder);
        CommandResult run = LinuxRunner.RunShell($"chmod +x '{exe}' && './{exe}' '{dir}/text.txt' '{dir}/empty.txt' '{dir}/long.txt'");
        var expected = new StringBuilder();
        (long Lines, long Words, long Bytes) total = (0, 0, 0);
        foreach ((string name, byte[] content) in files)
        {
            (long lines, long words, long bytes) = Count(content);
            expected.Append($"{lines,8}{words,8}{bytes,8} {dir}/{name}\n");
            total = (total.Lines + lines, total.Words + words, total.Bytes + bytes);
        }

        expected.Append($"{total.Lines,8}{total.Words,8}{total.Bytes,8} total\n");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(expected.ToString(), run.StdOut);

        CommandResult selected = LinuxRunner.RunShell($"'./{exe}' -lw '{dir}/text.txt' && './{exe}' -c < '{dir}/text.txt'");
        Assert.Equal($"       3       6 {dir}/text.txt\n      28\n", selected.StdOut);
    }

    /// <summary>
    /// WordCount's errors: a missing file is reported in the "wordcount: name: reason" form and
    /// the others are still counted (exit 1); an unknown option prints the usage (exit 2).
    /// </summary>
    [Fact]
    public void WordCount_reports_errors_like_a_unix_tool()
    {
        RequireSamples();
        string folder = Directory.CreateDirectory(Path.Combine(RunFolder, "wordcount-errors")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "one.txt"), "one line\n"u8.ToArray());
        string exe = Repo.Relative(WordCount);
        string dir = Repo.Relative(folder);

        CommandResult missing = LinuxRunner.RunShell($"chmod +x '{exe}' && './{exe}' '{dir}/missing.txt' '{dir}/one.txt'");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains($"wordcount: {dir}/missing.txt: No such file or directory", missing.StdErr);
        Assert.Contains($"       1       2       9 {dir}/one.txt", missing.StdOut);

        CommandResult option = LinuxRunner.RunShell($"'./{exe}' -x");
        Assert.Equal(2, option.ExitCode);
        Assert.Contains("wordcount: unknown option -x", option.StdErr);
        Assert.Contains("usage: wordcount [-lwc] [file ...]", option.StdErr);
    }

    // ---- HexDump ----

    /// <summary>HexDump is a static executable (no interpreter, no dynamic section) under 8 KB.</summary>
    [Fact]
    public void HexDump_is_a_small_static_executable()
    {
        RequireSamples();
        string text = sdk.ReadElf(HexDump, "-h", "-l", "-d");
        Assert.Contains("EXEC (Executable file)", text);
        Assert.DoesNotContain("INTERP", text);
        Assert.DoesNotContain("(NEEDED)", text);
        long size = new FileInfo(HexDump).Length;
        Assert.True(size < 8192, $"hexdump is {size} bytes");
    }

    /// <summary>
    /// HexDump prints xxd's default format: every byte value, a partial last line, from a file and
    /// from standard input. (The sample was also compared with xxd itself, byte for byte.)
    /// </summary>
    [Fact]
    public void HexDump_prints_xxd_lines()
    {
        RequireSamples();
        string folder = Directory.CreateDirectory(Path.Combine(RunFolder, "hexdump")).FullName;
        byte[] input = [.. Enumerable.Range(0, 256).Select(b => (byte)b), .. "Hello, world!\n"u8];
        File.WriteAllBytes(Path.Combine(folder, "input.bin"), input);
        string exe = Repo.Relative(HexDump);
        string file = Repo.Relative(Path.Combine(folder, "input.bin"));

        CommandResult run = LinuxRunner.RunShell($"chmod +x '{exe}' && './{exe}' '{file}'");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Xxd(input), run.StdOut);
        Assert.Equal(Xxd(input), LinuxRunner.RunShell($"'./{exe}' < '{file}'").StdOut);
    }

    /// <summary>HexDump's errors: a missing file (exit 1, with the reason) and too many arguments (exit 2).</summary>
    [Fact]
    public void HexDump_reports_errors_without_libc()
    {
        RequireSamples();
        string exe = Repo.Relative(HexDump);
        CommandResult missing = LinuxRunner.RunShell($"chmod +x '{exe}' && './{exe}' no-such-file");
        Assert.Equal(1, missing.ExitCode);
        Assert.Equal("hexdump: no-such-file: No such file or directory\n", missing.StdErr);
        CommandResult usage = LinuxRunner.RunShell($"'./{exe}' a b");
        Assert.Equal(2, usage.ExitCode);
        Assert.Equal("usage: hexdump [file]\n", usage.StdErr);
    }

    /// <summary>HexDump's WASI command (main.wasi.s, the wasi-wasm build): a module of a few kilobytes that imports WASI alone.</summary>
    private static readonly string HexDumpWasi = Repo.Sample("HexDump", "bin", "Release", "freestanding", "wasi-wasm", "hexdump.wasm");

    /// <summary>
    /// HexDump's WASI build is a WASI command under 4 KB: it exports _start and its memory, and
    /// imports nothing but wasi_snapshot_preview1's functions. Every machine checks it, wasmtime
    /// or not.
    /// </summary>
    [Fact]
    public void HexDump_wasi_is_a_small_wasi_command()
    {
        RequireSamples();
        WasmBinary module = WasmBinary.Read(HexDumpWasi);
        Assert.Contains("_start", module.ExportedFunctions);
        Assert.All(module.Imports, import => Assert.Equal("wasi_snapshot_preview1", import.Module));
        Assert.Contains(module.Imports, import => import.Name == "path_open");
        long size = new FileInfo(HexDumpWasi).Length;
        Assert.True(size < 4096, $"hexdump.wasm is {size} bytes");
    }

    /// <summary>
    /// HexDump's WASI build prints the same bytes as xxd and as the Linux build: from a file under
    /// the folder wasmtime preopened, from standard input, and from "-". The large input (200,000
    /// bytes) crosses the 64 KiB input buffer and the output buffer's flushes, which a short one
    /// never reaches. (Compared with xxd itself too, byte for byte, when it was written.)
    /// </summary>
    [Fact]
    public void HexDump_wasi_prints_xxd_lines()
    {
        RequireSamples();
        RequireWasmtime();
        string folder = Directory.CreateDirectory(Path.Combine(RunFolder, "hexdump-wasi")).FullName;
        byte[] input = [.. Enumerable.Range(0, 256).Select(b => (byte)b), .. "Hello, world!\n"u8];
        var random = new Random(7);
        byte[] large = new byte[200_000];
        random.NextBytes(large);
        File.WriteAllBytes(Path.Combine(folder, "input.bin"), input);
        File.WriteAllBytes(Path.Combine(folder, "large.bin"), large);

        foreach ((string name, byte[] bytes) in new[] { ("input.bin", input), ("large.bin", large) })
        {
            // --dir . preopens the working directory as fd 3, where a relative name opens.
            CommandResult run = Command.Run("wasmtime", ["run", "--dir", ".", HexDumpWasi, name], folder);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal(Xxd(bytes), run.StdOut);
        }

        CommandResult stdin = Command.RunWithInput("wasmtime", ["run", HexDumpWasi], input, folder);
        Assert.True(stdin.ExitCode == 0, stdin.Output);
        Assert.Equal(Xxd(input), stdin.StdOut);
        CommandResult dash = Command.RunWithInput("wasmtime", ["run", HexDumpWasi, "-"], input, folder);
        Assert.Equal(Xxd(input), dash.StdOut);
    }

    /// <summary>
    /// HexDump's WASI errors, with main.asm's words where Linux has them: a missing file (exit 1),
    /// too many arguments (exit 2), a directory ("Is a directory", which WASI would only report at
    /// the read as EBADF), a name outside the preopened folder (wasmtime: EPERM), and no folder
    /// preopened at all.
    /// </summary>
    [Fact]
    public void HexDump_wasi_reports_errors()
    {
        RequireSamples();
        RequireWasmtime();
        string folder = Directory.CreateDirectory(Path.Combine(RunFolder, "hexdump-wasi-errors")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "file.txt"), "x");
        (string[] Arguments, int Exit, string StdErr)[] cases =
        [
            (["--dir", ".", HexDumpWasi, "no-such-file"], 1, "hexdump: no-such-file: No such file or directory\n"),
            (["--dir", ".", HexDumpWasi, "a", "b"], 2, "usage: hexdump [file]\n"),
            (["--dir", ".", HexDumpWasi, "sub"], 1, "hexdump: sub: Is a directory\n"),
            (["--dir", ".", HexDumpWasi, "../file.txt"], 1, "hexdump: ../file.txt: Operation not permitted\n"),
            ([HexDumpWasi, "file.txt"], 1, "hexdump: file.txt: No folder preopened: run it with --dir .\n"),
        ];
        foreach ((string[] arguments, int exit, string stderr) in cases)
        {
            CommandResult run = Command.Run("wasmtime", ["run", .. arguments], folder);
            Assert.True(run.ExitCode == exit, $"{string.Join(' ', arguments)}: exit {run.ExitCode}, {run.Output}");
            Assert.Equal(stderr, run.StdErr);
        }
    }

    /// <summary>
    /// "dotnet run -r wasi-wasm" builds HexDump's WASI command and runs it in wasmtime, in the
    /// folder dotnet run was started from, with the program's arguments after "--": the same dump
    /// as xxd's, and HexDump's exit status.
    /// </summary>
    [Fact]
    public void HexDump_wasi_runs_with_dotnet_run()
    {
        RequireSamples();
        RequireWasmtime();
        string folder = Directory.CreateDirectory(Path.Combine(RunFolder, "hexdump-wasi-run")).FullName;
        byte[] input = "dotnet run, wasmtime, WASI\n"u8.ToArray();
        string file = Path.Combine(folder, "run.bin");
        File.WriteAllBytes(file, input);
        // dotnet run works in the repository root (the test environment's working directory), so
        // the file goes by its path relative to it, inside the folder wasmtime preopens.
        CommandResult run = sdk.DotnetRun("samples/HexDump/HexDump.asmproj", ["-c", "Release", "-r", "wasi-wasm"], Repo.Relative(file));
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.EndsWith(Xxd(input), run.StdOut.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Crc32C.Browser, the Crc32C sample in a .NET browser-wasm app: libcrc32c's WebAssembly half
    /// (crc32c.wasm.s) reaches the app through Crc32C.Net, a .NET library, and is linked into its
    /// runtime; the app's verify mode checks it against .NET's BitOperations.Crc32C and the same
    /// slicing-by-8 in C# on lengths 0-4096 at alignments 0-7, under Node. Needs .NET's native
    /// WebAssembly build for net10.0 (CI's Linux x64 and Windows x64 runners), like the other .NET
    /// WebAssembly app tests; the timing mode is run by hand (results/).
    /// </summary>
    [Fact]
    public void Crc32C_browser_app_agrees_with_dotnet()
    {
        string bundle = BuildCrc32CBrowser();
        CommandResult run = Command.Run("node", ["main.mjs", "verify"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("libcrc32c path Portable", run.StdOut, StringComparison.Ordinal);
        Assert.Contains("verified: 123456789 = 0xE3069283; asm, .NET and C# agree", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page's path, without a browser: "node main.mjs hash &lt;file&gt;" starts the runtime
    /// without Main and calls Page.Hash through [JSExport], as index.html does, so the export, the
    /// bytes handed from JavaScript to .NET, and the report the page reads are checked here. The
    /// file is the bundle's own index.html (also checking that the build deployed the page); each
    /// implementation's line must carry the checksum computed in C#. A missing file is exit 1 with
    /// Node's message, a missing argument exit 2.
    /// </summary>
    [Fact]
    public void Crc32C_browser_app_hashes_a_file_through_the_page_export()
    {
        string bundle = BuildCrc32CBrowser();
        string page = Path.Combine(bundle, "index.html");
        Assert.True(File.Exists(page), "the build deployed no index.html into the bundle (WasmExtraFilesToDeploy)");
        string expected = Crc32CHex(File.ReadAllBytes(page));

        CommandResult hash = Command.Run("node", ["main.mjs", "hash", "index.html"], bundle);
        Assert.True(hash.ExitCode == 0, hash.Output);
        Assert.Contains($"index.html: {new FileInfo(page).Length} bytes (interpreter, libcrc32c path Portable)", hash.StdOut, StringComparison.Ordinal);
        foreach (string name in new[] { "asm (crc32c.wasm.s)", ".NET BitOperations.Crc32C", "C# slicing-by-8" })
        {
            Assert.Matches($@"(?m)^  {Regex.Escape(name)} +{expected} +\d+\.\d MB/s$", hash.StdOut.Replace("\r\n", "\n"));
        }

        Assert.Contains("the three agree", hash.StdOut, StringComparison.Ordinal);

        CommandResult missing = Command.Run("node", ["main.mjs", "hash", "no-such-file"], bundle);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("hash: no-such-file: ENOENT", missing.StdErr, StringComparison.Ordinal);
        CommandResult usage = Command.Run("node", ["main.mjs", "hash"], bundle);
        Assert.Equal(2, usage.ExitCode);
        Assert.Contains("usage: node main.mjs hash <file>", usage.StdErr, StringComparison.Ordinal);
    }

    // ---- The Wasm Demos (docs/demos): three kernels in hand-written WebAssembly ----

    /// <summary>The three demo kernels, by project (samples/WasmDemos/&lt;name&gt;) and module name.</summary>
    private static readonly (string Project, string Module)[] WasmDemos = [("Life", "life"), ("Swarm", "swarm"), ("Ray", "ray")];

    /// <summary>A demo kernel's module as the samples' build wrote it.</summary>
    /// <param name="project">The project's folder and name (Life, Swarm, Ray).</param>
    /// <param name="module">The module's name (life, swarm, ray).</param>
    /// <returns>The full path of the .wasm file.</returns>
    private static string WasmDemoModule(string project, string module) =>
        Repo.Sample("WasmDemos", project, "bin", "Release", "freestanding", "browser-wasm", module + ".wasm");

    /// <summary>
    /// The demo kernels do what their pages say, under Node (samples/WasmDemos/check.mjs): Life
    /// steps the whole 4096 x 4096 torus exactly as a cell-by-cell reference does (three
    /// generations of a random board, a glider across the wrapped corner, a blinker), and draws,
    /// pans and zooms; the swarm's step equals a single-precision reference bit for bit for all
    /// 120,000 particles, pulled and pushed, and its hand-written sine follows Math.sin; the ray
    /// marcher's finished picture equals a reference renderer that repeats its every f32
    /// operation, bit for bit, also after a turn, a zoom and in the dark colours. Every machine:
    /// the modules are built by the bundled tools, and Node runs them.
    /// </summary>
    [Fact]
    public void Wasm_demo_kernels_pass_their_checks()
    {
        RequireSamples();
        if (!Command.OnPath("node"))
        {
            Assert.Skip("No Node on PATH to run the Wasm Demos' kernels here");
        }

        CommandResult run = Command.Run("node", ["check.mjs", .. WasmDemos.Select(d => WasmDemoModule(d.Project, d.Module))], Repo.Sample("WasmDemos"));
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("all passed", run.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", run.StdOut, StringComparison.Ordinal);
        // Each kernel's checks ran: a missing module argument would skip its group silently.
        foreach ((_, string module) in WasmDemos)
        {
            Assert.Contains($"ok {module}: ", run.StdOut, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The documentation site serves the modules this repository builds: docs/demos/&lt;module&gt;.wasm
    /// is a copy of the samples' Release build, byte for byte (AsmSdk's WebAssembly builds are
    /// reproducible), so a change to a kernel that is not copied to the docs fails here, naming
    /// the copy to make. And the overview page's sizes are the modules' own, to the 0.1 KB it gives.
    /// </summary>
    [Fact]
    public void Wasm_demos_on_the_docs_site_are_the_built_modules()
    {
        RequireSamples();
        string overview = File.ReadAllText(Path.Combine(Repo.Root, "docs", "demos", "index.md"));
        foreach ((string project, string module) in WasmDemos)
        {
            string built = WasmDemoModule(project, module), served = Path.Combine(Repo.Root, "docs", "demos", module + ".wasm");
            Assert.True(File.Exists(served), $"docs/demos/{module}.wasm is missing");
            Assert.True(File.ReadAllBytes(built).AsSpan().SequenceEqual(File.ReadAllBytes(served)),
                $"docs/demos/{module}.wasm is not this build's module: copy {Repo.Relative(built)} there");
            string size = (new FileInfo(built).Length / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains($"`{module}.wasm`, {size} KB", overview, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Builds Crc32C.Browser in Release once per run and returns its bundle folder, or skips (fails,
    /// when required) where Node or .NET's native WebAssembly build is missing.
    /// </summary>
    /// <returns>bin/Release/net10.0/browser-wasm/AppBundle of the sample.</returns>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the build fails or the run is required.</exception>
    private string BuildCrc32CBrowser()
    {
        WebAssemblyTests.RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        string project = Repo.Sample("Crc32C.Browser", "Crc32C.Browser.csproj");
        WebAssemblyTests.RequireWasmNativeBuild(sdk, project, "UsingBrowserRuntimeWorkload");
        CommandResult build = sdk.Once("build Crc32C.Browser", () => sdk.Dotnet("build", project, "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        return Repo.Sample("Crc32C.Browser", "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
    }

    /// <summary>
    /// CRC-32C in zlib's convention (the complements around the register), from .NET's
    /// BitOperations.Crc32C one byte at a time: an independent reference for the sample's output.
    /// </summary>
    /// <param name="bytes">The data.</param>
    /// <returns>The checksum as 8 uppercase hex digits, as the sample prints it.</returns>
    private static string Crc32CHex(byte[] bytes)
    {
        uint crc = ~0u;
        foreach (byte b in bytes)
        {
            crc = System.Numerics.BitOperations.Crc32C(crc, b);
        }

        return (~crc).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Skips the calling test when wasmtime is not on PATH.</summary>
    /// <exception cref="Exception">Xunit's skip exception.</exception>
    private static void RequireWasmtime()
    {
        if (!Command.OnPath("wasmtime"))
        {
            Assert.Skip("No wasmtime on PATH to run WASI programs here (https://wasmtime.dev)");
        }
    }

    /// <summary>
    /// 200,000 bytes for WordCount: one 100,000-byte word that crosses the 64 KiB read boundary,
    /// then lines that mix every whitespace byte.
    /// </summary>
    /// <returns>The bytes.</returns>
    private static byte[] LongText()
    {
        var bytes = new List<byte>(200_000);
        bytes.AddRange(Enumerable.Repeat((byte)'a', 100_000));
        string[] pieces = ["alpha", " ", "beta", "\t", "gamma", "\n", "\v", "delta", "\f", "\r", "  ", "epsilon\n"];
        for (int i = 0; bytes.Count < 200_000; i++)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(pieces[i % pieces.Length]));
        }

        return bytes.Take(200_000).ToArray();
    }

    /// <summary>
    /// The counts WordCount must print, computed in C#: newline bytes; words as runs of bytes that
    /// are not C-locale whitespace (space, \t, \n, \v, \f, \r); bytes.
    /// </summary>
    /// <param name="data">The file's content.</param>
    /// <returns>Lines, words and bytes.</returns>
    private static (long Lines, long Words, long Bytes) Count(byte[] data)
    {
        long lines = 0, words = 0;
        bool space = true;
        foreach (byte b in data)
        {
            bool isSpace = b == ' ' || b is >= 9 and <= 13;
            lines += b == '\n' ? 1 : 0;
            words += space && !isSpace ? 1 : 0;
            space = isSpace;
        }

        return (lines, words, data.Length);
    }

    /// <summary>xxd's default format, in C#: offset, 8 groups of 2 bytes, the bytes as text.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The dump, one line per 16 bytes.</returns>
    private static string Xxd(byte[] data)
    {
        var text = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int count = Math.Min(16, data.Length - offset);
            char[] hex = new string(' ', 40).ToCharArray();
            for (int i = 0; i < count; i++)
            {
                string pair = data[offset + i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
                int column = i / 2 * 5 + i % 2 * 2;     // a space after every second byte
                hex[column] = pair[0];
                hex[column + 1] = pair[1];
            }

            text.Append(offset.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)).Append(": ")
                .Append(hex).Append(' ');
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                text.Append(b is >= 0x20 and <= 0x7e ? (char)b : '.');
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}
