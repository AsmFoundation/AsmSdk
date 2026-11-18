#:package SharpCompress@1.0.0
// =============================================================================================
// fetch.cs: stages the bundled toolchain (NASM, ld.lld, llvm-ar, llvm-readobj) for one build
// machine. A .NET 10 file-based app; it replaced toolchain/fetch.py in AsmSdk 0.2.0.
//
// Usage (from the repository root):
//   dotnet run toolchain/fetch.cs -- --rid win-x64|linux-x64|osx-arm64
//                                    [--out artifacts/toolchain] [--cache artifacts/downloads]
//
// Writes <out>/<rid>/tools/ with the executables, their license texts and VERSIONS.txt.
// "dotnet pack toolchain/AsmFoundation.Toolchain.csproj -p:ToolchainRid=<rid>" then turns that
// folder into the AsmFoundation.Toolchain.<rid> package.
//
// Where each tool comes from (pinned in versions.json; every download is checked against its
// SHA-256 before it is opened):
//   - NASM on win-x64: the official win64 zip.
//   - NASM elsewhere: built from the official source tarball on the machine running this
//     program, which must therefore be the target OS (the release workflow runs one job per
//     RID). The official macOS binary is i386/x86_64 only and would need Rosetta on Apple
//     silicon; the Linux build is static, so it runs on any distribution.
//   - lld, llvm-ar, llvm-readobj: read out of the official LLVM release archive for the RID,
//     as a stream: only these members are kept from the 0.9-2 GB .tar.xz, nothing is unpacked.
//
// The .tar.xz archives are decoded by SharpCompress (pure managed; .NET has no xz decoder).
// Measured on the 901 MB Windows LLVM archive: 83.5 s against 78.0 s for Python's lzma, the
// decoder fetch.py used, with byte-identical tools.
//
// Downloads are cached by SHA-256 in --cache, so repeated runs (and CI with a cache) do not
// fetch the large LLVM archives again.
// =============================================================================================
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Compressors.Xz;

return await Fetch.MainAsync(args);

/// <summary>The toolchain staging steps; see the file header.</summary>
internal static class Fetch
{
    /// <summary>The build machines a toolchain package exists for.</summary>
    private static readonly string[] SupportedRids = ["win-x64", "linux-x64", "osx-arm64"];

    /// <summary>The HTTP client for every download (GitHub's release downloads redirect, which it follows).</summary>
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Parses the arguments, stages NASM and LLVM for one RID, and writes VERSIONS.txt.</summary>
    /// <param name="args">The command line (see the file header).</param>
    /// <returns>The exit code: 0 on success, 2 on a usage error, 1 on a failed step.</returns>
    public static async Task<int> MainAsync(string[] args)
    {
        string here = ThisDirectory();
        string repo = Path.GetFullPath(Path.Combine(here, ".."));
        string? rid = null;
        string output = Path.Combine(repo, "artifacts", "toolchain");
        string cache = Path.Combine(repo, "artifacts", "downloads");
        for (int i = 0; i < args.Length; i++)
        {
            // Every option takes a value; a missing one is a usage error, not an index error.
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--rid" when value is not null: rid = value; i++; break;
                case "--out" when value is not null: output = Path.GetFullPath(value); i++; break;
                case "--cache" when value is not null: cache = Path.GetFullPath(value); i++; break;
                default:
                    Console.Error.WriteLine($"usage: dotnet run toolchain/fetch.cs -- --rid {string.Join('|', SupportedRids)} [--out DIR] [--cache DIR] (bad argument: {args[i]})");
                    return 2;
            }
        }

        if (rid is null || !SupportedRids.Contains(rid))
        {
            Console.Error.WriteLine($"--rid is required: one of {string.Join(", ", SupportedRids)}");
            return 2;
        }

        try
        {
            using JsonDocument pins = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(here, "versions.json")));
            JsonElement root = pins.RootElement;
            string tools = Path.Combine(output, rid, "tools");
            if (Directory.Exists(tools))
            {
                Directory.Delete(tools, recursive: true);
            }

            Directory.CreateDirectory(tools);
            Directory.CreateDirectory(cache);

            await StageNasmAsync(rid, root.GetProperty("nasm"), cache, tools);
            await StageLlvmAsync(rid, root.GetProperty("llvm"), cache, tools);
            JsonElement llvm = root.GetProperty("llvm");
            string llvmTools = string.Join(", ", llvm.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
            await File.WriteAllTextAsync(Path.Combine(tools, "VERSIONS.txt"),
                $"AsmFoundation.Toolchain.{rid} {root.GetProperty("packageVersion").GetString()}\n" +
                $"NASM {root.GetProperty("nasm").GetProperty("version").GetString()}\n" +
                $"LLVM {llvm.GetProperty("version").GetString()} ({llvmTools})\n");
            foreach (FileInfo file in new DirectoryInfo(tools).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                Console.WriteLine($"{file.Length / 1048576.0,8:F1} MB  {file.Name}");
            }

            return 0;
        }
        catch (FetchException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>Puts nasm (nasm.exe on Windows) and NASM's license into the tools folder.</summary>
    /// <param name="rid">The RID being staged.</param>
    /// <param name="pins">The "nasm" object of versions.json.</param>
    /// <param name="cache">The download cache folder.</param>
    /// <param name="tools">The destination folder.</param>
    /// <returns>A task that completes when NASM is staged.</returns>
    /// <exception cref="FetchException">A download fails verification, the source build fails, or this OS cannot build the RID.</exception>
    /// <exception cref="HttpRequestException">A download fails.</exception>
    private static async Task StageNasmAsync(string rid, JsonElement pins, string cache, string tools)
    {
        if (rid == "win-x64")
        {
            string zip = await DownloadAsync(pins.GetProperty("win-x64"), cache);
            using ZipArchive archive = ZipFile.OpenRead(zip);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Name is "nasm.exe" or "LICENSE")
                {
                    entry.ExtractToFile(Path.Combine(tools, entry.Name == "LICENSE" ? "LICENSE.nasm.txt" : entry.Name), overwrite: true);
                }
            }

            return;
        }

        // Building from source needs the target OS: refuse early instead of producing a binary
        // for the wrong platform.
        bool onTarget = rid == "linux-x64" ? OperatingSystem.IsLinux() : OperatingSystem.IsMacOS();
        if (!onTarget)
        {
            throw new FetchException($"NASM for {rid} is built from source and must be staged on {(rid == "linux-x64" ? "Linux" : "macOS")}");
        }

        string tarball = await DownloadAsync(pins.GetProperty("sources"), cache);
        string work = Directory.CreateTempSubdirectory("asmsdk-nasm-").FullName;
        try
        {
            // TarFile keeps every entry inside the destination (it refuses ../ and absolute
            // paths) and restores the Unix modes, so ./configure and its helpers stay executable.
            await using (FileStream file = File.OpenRead(tarball))
            await using (var xz = new XZStream(file))
            {
                await TarFile.ExtractToDirectoryAsync(xz, work, overwriteFiles: false);
            }

            string source = Directory.GetDirectories(work).Single();
            var environment = new Dictionary<string, string>();
            if (rid == "linux-x64")
            {
                environment["LDFLAGS"] = "-static"; // runs on any distribution, whatever its glibc
            }

            Run("./configure", ["--disable-werror"], source, environment);
            Run("make", ["-j4", "nasm"], source, environment);
            File.Copy(Path.Combine(source, "nasm"), Path.Combine(tools, "nasm"), overwrite: true);
            File.Copy(Path.Combine(source, "LICENSE"), Path.Combine(tools, "LICENSE.nasm.txt"), overwrite: true);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        MakeExecutable(Path.Combine(tools, "nasm"));
        StripSymbols(rid, Path.Combine(tools, "nasm"));
    }

    /// <summary>Reads the pinned LLVM tools for the RID out of its release archive, plus LLVM's license.</summary>
    /// <param name="rid">The RID being staged (selects the archive).</param>
    /// <param name="pins">The "llvm" object of versions.json.</param>
    /// <param name="cache">The download cache folder.</param>
    /// <param name="tools">The destination folder.</param>
    /// <returns>A task that completes when the tools are staged.</returns>
    /// <exception cref="FetchException">A download fails verification, or a tool is missing from the archive.</exception>
    /// <exception cref="HttpRequestException">A download fails.</exception>
    /// <remarks>
    /// The archive is read as one stream, member by member; only bin/ entries that are wanted
    /// (or that a wanted link may point to) are kept in memory. Release archives ship ld.lld
    /// and similar names as symbolic links to lld, so a wanted name that is a link is resolved
    /// to the file it points to, following a chain of links at most once per name.
    /// </remarks>
    private static async Task StageLlvmAsync(string rid, JsonElement pins, string cache, string tools)
    {
        string exe = rid.StartsWith("win", StringComparison.Ordinal) ? ".exe" : "";
        HashSet<string> wanted = pins.GetProperty("tools").EnumerateArray().Select(t => t.GetString() + exe).ToHashSet(StringComparer.Ordinal);
        string archive = await DownloadAsync(pins.GetProperty(rid), cache);
        var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (FileStream file = File.OpenRead(archive))
        await using (var xz = new XZStream(file))
        await using (var tar = new TarReader(xz))
        {
            while (await tar.GetNextEntryAsync() is { } entry)
            {
                string[] parts = entry.Name.Split('/');
                if (parts.Length < 3 || parts[^2] != "bin")
                {
                    continue;
                }

                string name = parts[^1];
                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    links[name] = entry.LinkName.Split('/')[^1];
                    continue;
                }

                // Keep link targets too: a wanted name may turn out to be a link to this file,
                // and the archive lists links and files in no particular order.
                bool candidate = wanted.Contains(name) || links.ContainsValue(name)
                                 || name.StartsWith("lld", StringComparison.Ordinal)
                                 || name.StartsWith("llvm-ar", StringComparison.Ordinal)
                                 || name.StartsWith("llvm-readobj", StringComparison.Ordinal);
                if (candidate && entry.DataStream is { } data)
                {
                    using var copy = new MemoryStream();
                    await data.CopyToAsync(copy);
                    found[name] = copy.ToArray();
                }
            }
        }

        foreach (string name in wanted.Order(StringComparer.Ordinal))
        {
            string source = name;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!found.ContainsKey(source) && links.TryGetValue(source, out string? target) && seen.Add(source))
            {
                source = target;
            }

            if (!found.TryGetValue(source, out byte[]? content))
            {
                throw new FetchException($"{name} not found in {Path.GetFileName(archive)}");
            }

            string destination = Path.Combine(tools, name);
            await File.WriteAllBytesAsync(destination, content);
            MakeExecutable(destination);
            StripSymbols(rid, destination);
        }

        File.Copy(await DownloadAsync(pins.GetProperty("license"), cache), Path.Combine(tools, "LICENSE.llvm.txt"), overwrite: true);
    }

    /// <summary>Returns a verified local copy of a pinned download, fetching it only when it is not cached.</summary>
    /// <param name="pin">A versions.json object with "url" and "sha256".</param>
    /// <param name="cache">The folder holding downloads, named by hash.</param>
    /// <returns>The path of the verified file.</returns>
    /// <exception cref="FetchException">The downloaded file's hash differs from the pin (it is deleted, nothing is read from it).</exception>
    /// <exception cref="HttpRequestException">The download fails.</exception>
    /// <remarks>
    /// The cache key is the expected hash, so a changed pin never reuses a stale file, and a
    /// corrupted or tampered cache entry is detected and replaced. The download goes to a .part
    /// file, renamed only once verified: an interrupted run never leaves a file that looks done.
    /// </remarks>
    private static async Task<string> DownloadAsync(JsonElement pin, string cache)
    {
        string url = pin.GetProperty("url").GetString()!;
        string sha256 = pin.GetProperty("sha256").GetString()!;
        string target = Path.Combine(cache, $"{sha256}-{Path.GetFileName(new Uri(url).LocalPath).Replace("%2B", "+", StringComparison.Ordinal)}");
        if (File.Exists(target) && await Sha256Async(target) == sha256)
        {
            Console.WriteLine($"cached   {Path.GetFileName(target)}");
            return target;
        }

        string partial = target + ".part";
        Console.WriteLine($"download {url}");
        using (HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using FileStream output = File.Create(partial);
            await response.Content.CopyToAsync(output);
        }

        string actual = await Sha256Async(partial);
        if (actual != sha256)
        {
            File.Delete(partial);
            throw new FetchException($"SHA-256 mismatch for {url}: expected {sha256}, got {actual}");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>The lowercase hex SHA-256 of a file, streamed (multi-gigabyte archives are fine).</summary>
    /// <param name="path">The file.</param>
    /// <returns>64 lowercase hex characters.</returns>
    private static async Task<string> Sha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    /// <summary>Runs a build step and fails the staging when it fails.</summary>
    /// <param name="program">The program (./configure, make, strip).</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="directory">The working directory.</param>
    /// <param name="environment">Variables added to the inherited environment.</param>
    /// <exception cref="FetchException">The program exited with a non-zero status.</exception>
    /// <remarks>Its output goes to this program's own (configure and make are verbose only when they fail).</remarks>
    private static void Run(string program, string[] arguments, string directory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = directory, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new FetchException($"{program} {string.Join(' ', arguments)} failed with exit code {process.ExitCode} in {directory}");
        }
    }

    /// <summary>Sets the execute bits of a file (nothing to do on Windows).</summary>
    /// <param name="path">The file.</param>
    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Removes symbol tables and debug information from a Linux executable, when possible.</summary>
    /// <param name="rid">The RID being staged.</param>
    /// <param name="path">The executable, stripped in place.</param>
    /// <exception cref="FetchException">strip failed on a file it should handle.</exception>
    /// <remarks>
    /// The official LLVM Linux binaries keep their symbol tables (19 MB of lld's 203 MB) and the
    /// static NASM build keeps its DWARF; neither is needed to run the tool. Only linux-x64 is
    /// stripped, on Linux with binutils' strip: a Mach-O binary must not be stripped here,
    /// because that invalidates its code signature and Apple silicon refuses to run an unsigned
    /// binary; Windows PE files carry no symbols (they live in PDBs).
    /// </remarks>
    private static void StripSymbols(string rid, string path)
    {
        if (rid != "linux-x64" || !OperatingSystem.IsLinux() || !OnPath("strip"))
        {
            return;
        }

        Run("strip", ["--strip-all", path], Path.GetDirectoryName(path)!);
    }

    /// <summary>Whether a program is on PATH.</summary>
    /// <param name="name">The program's name.</param>
    /// <returns>True when PATH holds a file of that name.</returns>
    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, name)));

    /// <summary>The folder of this source file (toolchain/), whatever the working directory.</summary>
    /// <param name="path">Filled in by the compiler with this file's path.</param>
    /// <returns>The folder.</returns>
    /// <remarks>A file-based app is compiled from its source path, so the compiler-supplied path is the script's own.</remarks>
    private static string ThisDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>The HTTP client: a User-Agent (GitHub and nasm.us expect one) and no overall timeout (archives take minutes).</summary>
    /// <returns>The client.</returns>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AsmFoundation-toolchain-fetch");
        return client;
    }
}

/// <summary>A staging step failed for a reason worth one line (a hash, a missing tool, a failed build).</summary>
/// <param name="message">What failed.</param>
internal sealed class FetchException(string message) : Exception(message);
