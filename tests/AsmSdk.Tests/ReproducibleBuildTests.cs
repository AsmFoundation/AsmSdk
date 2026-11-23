using System.Security.Cryptography;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// Reproducible Release builds: the same sources built in two different checkout folders, each
/// with its own NuGet cache, give byte-identical outputs for all eight RIDs: shared and static
/// libraries, freestanding and libc, and libc programs.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// <para>
/// What makes them so: the SDK passes each source to NASM and llvm-mc by its path relative to
/// the project folder, so no object records the folder (ELF's file symbol, COFF's .file record),
/// and assembles its own startup and C library sources in their folder by file name, so no
/// program records where the NuGet cache is; NASM runs with --reproducible (no timestamp in a
/// COFF object, which a .lib keeps verbatim); lld-link links with -Brepro; llvm-ar writes
/// deterministic archives. Before, the absolute source path made the objects of two checkouts
/// differ, and a Linux libc program carried the cache's path of crt1.asm in its symbol table.
/// </para>
/// <para>
/// Debug builds still differ between folders: their DWARF records the folder, and NASM 3.02 has
/// no prefix map. A Release build with DebugSymbols=true does too.
/// </para>
/// </remarks>
public sealed class ReproducibleBuildTests(SdkUnderTest sdk)
{
    /// <summary>
    /// The test projects copied into each checkout: a freestanding shared library and the static
    /// library it links (Everywhere, EverywhereStatic), a libc shared library and a libc program
    /// (both link the SDK's own C library and startup objects), all for the eight RIDs.
    /// </summary>
    private static readonly string[] Projects = ["Everywhere", "EverywhereStatic", "EverywhereLibcLibrary", "EverywhereLibc"];

    /// <summary>The projects built in each checkout: EverywhereStatic is built through Everywhere's reference.</summary>
    private static readonly string[] Built = ["Everywhere", "EverywhereLibcLibrary", "EverywhereLibc"];

    /// <summary>
    /// Copies the <see cref="Projects"/> into two folders of different depth, one with a space in
    /// its path, builds them in Release in each, the second checkout with a NuGet cache of its own
    /// (so the SDK and the toolchain sit at another path too, as on another machine), and
    /// compares every file of every project's bin/Release, byte for byte.
    /// </summary>
    [Fact]
    public void A_release_build_is_the_same_in_any_folder()
    {
        string root = Path.Combine(Repo.Artifacts, "test-run", "reproducible");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        string first = CheckOut(Path.Combine(root, "a"));
        string second = CheckOut(Path.Combine(root, "another checkout", "deeper"));
        var otherCache = new Dictionary<string, string?>(sdk.TestEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = Path.Combine(root, "another nuget cache"),
        };
        foreach ((string checkout, IReadOnlyDictionary<string, string?> environment) in new (string, IReadOnlyDictionary<string, string?>)[] { (first, sdk.TestEnvironment), (second, otherCache) })
        {
            foreach (string project in Built)
            {
                CommandResult build = Command.Run("dotnet",
                    ["build", Path.Combine(checkout, project, project + ".asmproj"), "-c", "Release", "-nologo", "-nodeReuse:false"],
                    Repo.Root, environment);
                Assert.True(build.ExitCode == 0, $"{checkout}, {project}: {build.Tail()}");
            }
        }

        foreach (string project in Projects)
        {
            Dictionary<string, string> firstFiles = Hashes(Path.Combine(first, project, "bin", "Release"));
            Dictionary<string, string> secondFiles = Hashes(Path.Combine(second, project, "bin", "Release"));
            Assert.NotEmpty(firstFiles);
            Assert.Equal(firstFiles.Keys.Order(StringComparer.Ordinal), secondFiles.Keys.Order(StringComparer.Ordinal));
            string[] differing = firstFiles.Where(file => secondFiles[file.Key] != file.Value).Select(file => $"{project}/bin/Release/{file.Key}").ToArray();
            Assert.True(differing.Length == 0, $"differ between the two checkouts: {string.Join(", ", differing)}");
        }

        // Every RID's library and program is among what was compared.
        Dictionary<string, string> libraries = Hashes(Path.Combine(first, "Everywhere", "bin", "Release"));
        Dictionary<string, string> libcLibraries = Hashes(Path.Combine(first, "EverywhereLibcLibrary", "bin", "Release"));
        Dictionary<string, string> programs = Hashes(Path.Combine(first, "EverywhereLibc", "bin", "Release"));
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.Contains($"freestanding/{rid}/{CrossPlatformTests.LibraryFile(rid, "everywhere")}", libraries.Keys);
            Assert.Contains($"libc/{rid}/{CrossPlatformTests.LibraryFile(rid, "everywherelibc")}", libcLibraries.Keys);
            string program = rid.StartsWith("win-", StringComparison.Ordinal) ? "everywhere-libc.exe" : "everywhere-libc";
            Assert.Contains($"libc/{rid}/{program}", programs.Keys);
        }
    }

    /// <summary>
    /// Makes a checkout: copies of the <see cref="Projects"/> (their files, not their bin/ and
    /// obj/), and the files that make the folder a repository of its own, as tests/projects has
    /// them: global.json naming the SDK under test, nuget.config with the local feed (an absolute
    /// path, the copies being at another depth), and a Directory.Build.props that stops
    /// MSBuild's search before the AsmSdk repository's own settings.
    /// </summary>
    /// <param name="folder">The checkout folder (created).</param>
    /// <returns>The folder.</returns>
    private string CheckOut(string folder)
    {
        foreach (string project in Projects)
        {
            string target = Path.Combine(folder, project);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(Repo.TestProjectPath(project)))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        File.WriteAllText(Path.Combine(folder, "global.json"),
            $"{{ \"msbuild-sdks\": {{ \"AsmFoundation.Sdk\": \"{sdk.Version}\" }} }}\n");
        string feed = Path.Combine(Repo.Artifacts, "packages");
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local"><package pattern="AsmFoundation.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);
        return folder;
    }

    /// <summary>The SHA-256 of every file under a folder, by path relative to it with forward slashes.</summary>
    /// <param name="folder">The folder (empty result when it does not exist).</param>
    /// <returns>The hashes, hex.</returns>
    private static Dictionary<string, string> Hashes(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
                file => Path.GetRelativePath(folder, file).Replace('\\', '/'),
                file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
                StringComparer.Ordinal)
            : [];
}
