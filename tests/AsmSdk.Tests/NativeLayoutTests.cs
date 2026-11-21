using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// BinariesCopyPath: where a .NET project's output keeps the native libraries of its asm
/// references and asm packages, and that the program then loads them, on this machine, in every
/// way .NET deploys: portable, for one RID, self-contained, as a single file and with NativeAOT.
/// Also the two ways a .NET library's package delivers its asm binaries (a dependency, or inside
/// it with PrivateAssets="all").
/// </summary>
/// <remarks>
/// <para>
/// The projects (tests/projects/NetLayout*, NetAsmPackage) take each layout from a property on
/// the command line, so one project covers every value. Outputs go to artifacts/test-run/, a
/// folder per case, so no case sees another's files.
/// </para>
/// <para>
/// What each case proves beyond the file list: the program prints which RID's library it
/// loaded ("&lt;rid&gt; 42 &lt;ew_platform()&gt; &lt;ew_os()&gt;"), so a library left where nothing
/// looks fails with DllNotFoundException, and one of the wrong RID prints the wrong number.
/// </para>
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class NativeLayoutTests(SdkUnderTest sdk)
{
    /// <summary>The program that references Everywhere.asmproj directly.</summary>
    private static readonly string Layout = Repo.TestProjectPath("NetLayout", "NetLayout.csproj");

    /// <summary>The wrapper library over Everywhere.asmproj.</summary>
    private static readonly string LayoutLibrary = Repo.TestProjectPath("NetLayoutLibrary", "NetLayoutLibrary.csproj");

    /// <summary>The program that reaches Everywhere only through the wrapper.</summary>
    private static readonly string LayoutThrough = Repo.TestProjectPath("NetLayoutThrough", "NetLayoutThrough.csproj");

    /// <summary>The program that gets everything from the wrapper's package.</summary>
    private static readonly string LayoutPackage = Repo.TestProjectPath("NetLayoutPackage", "NetLayoutPackage.csproj");

    /// <summary>The program that references the asm package Test.Everywhere.</summary>
    private static readonly string AsmPackage = Repo.TestProjectPath("NetAsmPackage", "NetAsmPackage.csproj");

    /// <summary>Everywhere's library key in a deps.json: its PackageId and version.</summary>
    private const string EverywhereLibrary = "Test.Everywhere/1.0.0";

    /// <summary>
    /// Packs Everywhere as the package Test.Everywhere 1.0.0 into the local feed, once, for its
    /// consumers here and in ProgramTests.
    /// </summary>
    /// <param name="sdk">The SDK under test.</param>
    /// <returns>The pack's result.</returns>
    /// <remarks>
    /// With its static library linked in, not a dependency (EverywhereStaticPrivate=all):
    /// everywherestatic names no RID and cannot be packed alone, so a package that depended on it
    /// could not be restored.
    /// </remarks>
    internal static CommandResult PackEverywhere(SdkUnderTest sdk) =>
        sdk.Once("pack Everywhere into the feed", () => sdk.Dotnet("pack", Repo.TestProject("Everywhere"), "-c", "Release", "-p:EverywhereStaticPrivate=all"));

    /// <summary>A folder of this run under artifacts/test-run/.</summary>
    /// <param name="name">The folder's name.</param>
    /// <returns>The full path (not created: dotnet's -o creates it).</returns>
    private static string RunFolder(string name) => Path.Combine(Repo.Artifacts, "test-run", "layout-" + name);

    /// <summary>
    /// The argument that gives NetLayoutThrough alone an output folder (its OutDir, with the
    /// trailing separator OutDir needs). Not -o: that is every project's output folder, and
    /// NetLayoutLibrary would copy its own layout of the asm library there as well.
    /// </summary>
    /// <param name="output">The folder.</param>
    /// <returns>The -p argument.</returns>
    private static string ThroughOutput(string output) => $"-p:NetLayoutThroughOutput={output}{Path.DirectorySeparatorChar}";

    /// <summary>The line the programs print when they loaded the library of <paramref name="rid"/>.</summary>
    /// <param name="rid">The RID the program runs as (this machine's).</param>
    /// <returns>"&lt;rid&gt; 42 &lt;ew_platform()&gt; &lt;ew_os()&gt;".</returns>
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

    /// <summary>Where a layout puts one RID's library, relative to the output.</summary>
    /// <param name="folder">The folder pattern ("" for the app folder).</param>
    /// <param name="rid">The RID.</param>
    /// <returns>For example "native/win-x64/everywhere.dll".</returns>
    private static string LibraryPath(string folder, string rid) =>
        (folder.Length == 0 ? "" : folder.Replace("{rid}", rid) + "/") + CrossPlatformTests.LibraryFile(rid, "everywhere");

    /// <summary>Runs a program of an output folder with the .NET host of this machine.</summary>
    /// <param name="output">The output or publish folder.</param>
    /// <param name="assembly">The program's assembly file name.</param>
    /// <returns>The run's result.</returns>
    private static CommandResult RunHere(string output, string assembly) =>
        Command.Run("dotnet", [Path.Combine(output, assembly)], Repo.Root);

    /// <summary>Runs a published program's own executable (apphost, single file or NativeAOT).</summary>
    /// <param name="output">The publish folder.</param>
    /// <param name="name">The program's name, without an extension.</param>
    /// <returns>The run's result.</returns>
    private static CommandResult RunExecutable(string output, string name) =>
        Command.Run(Path.Combine(output, OperatingSystem.IsWindows() ? name + ".exe" : name), [], Repo.Root);

    /// <summary>Asserts that a program ran and loaded this machine's build of the library.</summary>
    /// <param name="run">The run.</param>
    /// <param name="rid">This machine's RID.</param>
    private static void AssertLoaded(CommandResult run, string rid)
    {
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(Expected(rid), run.StdOut.Trim());
    }

    /// <summary>The native assets a deps.json lists for one library, by kind.</summary>
    /// <param name="depsFile">The deps.json.</param>
    /// <param name="library">The library key ("Test.Everywhere/1.0.0").</param>
    /// <returns>Each asset's key with its localPath, under "runtimeTargets" or "native" (prefixed so).</returns>
    private static SortedDictionary<string, string> DepsAssets(string depsFile, string library)
    {
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        string target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        var assets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!deps.RootElement.GetProperty("targets").GetProperty(target).TryGetProperty(library, out JsonElement entry))
        {
            return assets;
        }

        foreach (string kind in new[] { "runtimeTargets", "native" })
        {
            if (entry.TryGetProperty(kind, out JsonElement list))
            {
                foreach (JsonProperty asset in list.EnumerateObject())
                {
                    assets[kind + ":" + asset.Name] = asset.Value.TryGetProperty("localPath", out JsonElement local) ? local.GetString()! : "";
                }
            }
        }

        return assets;
    }

    /// <summary>Whether a compiled assembly holds the generated loader class (its name is in the metadata strings).</summary>
    /// <param name="assembly">The assembly file.</param>
    /// <returns>True when "AsmNativeLibraryLoader" occurs in the file.</returns>
    private static bool HasLoader(string assembly) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(assembly)).Contains("AsmNativeLibraryLoader", StringComparison.Ordinal);

    /// <summary>Lists the files of a folder that are builds of Everywhere's library, relative and with "/".</summary>
    /// <param name="output">The folder.</param>
    /// <returns>The relative paths, sorted.</returns>
    private static List<string> EverywhereFiles(string output) =>
        Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileNameWithoutExtension(f) is "everywhere" or "libeverywhere" && Path.GetExtension(f) is ".dll" or ".so" or ".dylib")
            .Select(f => Path.GetRelativePath(output, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// BinariesCopyPath on the ProjectReference: a portable build puts each of the eight RIDs'
    /// libraries where the value says (presets and folders alike, "./a//b/{RID}/" read as
    /// a/b/{rid}), deps.json lists each with its localPath, the generated loader knows the
    /// folder, and the program loads this machine's build.
    /// </summary>
    /// <param name="value">The value as written.</param>
    /// <param name="folder">The folder it means.</param>
    [Theory]
    [InlineData("native/{rid}", "native/{rid}")]
    [InlineData("RidFolders", "{rid}")]
    [InlineData("Runtimes", "runtimes/{rid}/native")]
    [InlineData("./lib//Asm/{RID}/", "lib/Asm/{rid}")]
    public void A_reference_layout_places_every_rid_and_deps_json_names_it(string value, string folder)
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("reference-" + folder.Replace("{rid}", "rid").Replace('/', '-'));
        CommandResult build = sdk.Once($"build NetLayout {value}", () => sdk.Dotnet("build", Layout, "-c", "Release", $"-p:NetLayoutReference={value}", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());

        List<string> expected = CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath(folder, r)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, EverywhereFiles(output));
        SortedDictionary<string, string> deps = DepsAssets(Path.Combine(output, "NetLayout.deps.json"), EverywhereLibrary);
        Assert.Equal(expected.Select(p => "runtimeTargets:" + p), deps.Keys);
        Assert.All(deps, entry => Assert.Equal(entry.Key["runtimeTargets:".Length..], entry.Value));
        string loader = File.ReadAllText(Repo.TestProjectPath("NetLayout", "obj", "Release", "net10.0", "AsmNativeLibraryLoader.g.cs"));
        Assert.Contains($"new[] {{ \"everywhere\", \"{folder}\" }}", loader);
        AssertLoaded(RunHere(output, "NetLayout.dll"), rid);
    }

    /// <summary>The BinariesCopyPath property lays out every asm reference of the project.</summary>
    [Fact]
    public void The_property_lays_out_every_asm_reference()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("property");
        CommandResult build = sdk.Once("build NetLayout property", () => sdk.Dotnet("build", Layout, "-c", "Release", "-p:BinariesCopyPath=native/{rid}", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath("native/{rid}", r)).Order(StringComparer.Ordinal), EverywhereFiles(output));
        AssertLoaded(RunHere(output, "NetLayout.dll"), rid);
    }

    /// <summary>
    /// A program that says nothing gets the layout its class library chose on its own reference
    /// (often the only place the asm project is referenced), and loads the library from there.
    /// </summary>
    [Fact]
    public void A_library_choice_reaches_a_program_that_says_nothing()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("library-choice");
        CommandResult build = sdk.Once("build NetLayoutThrough library native/{rid}", () => sdk.Dotnet("build", LayoutThrough, "-c", "Release", "-p:NetLayoutLibraryReference=native/{rid}", ThroughOutput(output)));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath("native/{rid}", r)).Order(StringComparer.Ordinal), EverywhereFiles(output));
        AssertLoaded(RunHere(output, "NetLayoutThrough.dll"), rid);
    }

    /// <summary>
    /// The deploying program decides: its own BinariesCopyPath property beats the choice of the
    /// class library it gets the asm library through.
    /// </summary>
    [Fact]
    public void The_program_property_beats_the_library_choice()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("program-wins");
        CommandResult build = sdk.Once("build NetLayoutThrough program wins", () =>
            sdk.Dotnet("build", LayoutThrough, "-c", "Release", "-p:NetLayoutLibraryReference=native/{rid}", "-p:BinariesCopyPath=RidFolders", ThroughOutput(output)));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath("{rid}", r)).Order(StringComparer.Ordinal), EverywhereFiles(output));
        AssertLoaded(RunHere(output, "NetLayoutThrough.dll"), rid);
    }

    /// <summary>
    /// dotnet build -r: only this RID's library, in the folder (no {rid} needed for one RID), a
    /// plain native deps.json asset with its localPath (the host keeps only a file name
    /// otherwise), and the program loads it.
    /// </summary>
    [Fact]
    public void A_rid_specific_build_keeps_its_rid_in_a_subfolder_and_loads_it()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("rid-specific");
        CommandResult build = sdk.Once($"build NetLayout -r {rid} native", () => sdk.Dotnet("build", Layout, "-c", "Release", "-r", rid, "-p:NetLayoutReference=native", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        string library = LibraryPath("native", rid);
        Assert.Equal(new[] { library }, EverywhereFiles(output));
        Assert.Equal(new SortedDictionary<string, string> { ["native:" + library] = library }, DepsAssets(Path.Combine(output, "NetLayout.deps.json"), EverywhereLibrary));
        AssertLoaded(RunHere(output, "NetLayout.dll"), rid);
    }

    /// <summary>A self-contained publish loads its library from the subfolder (deps.json's localPath, measured: a self-contained app ignores runtimeTargets).</summary>
    [Fact]
    public void A_self_contained_publish_loads_from_a_subfolder()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("self-contained");
        CommandResult publish = sdk.Once($"publish NetLayout -r {rid} self-contained", () =>
            sdk.Dotnet("publish", Layout, "-c", "Release", "-r", rid, "--self-contained", "-p:NetLayoutReference=native/{rid}", "-o", output));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.Equal(new[] { LibraryPath("native/{rid}", rid) }, EverywhereFiles(output));
        AssertLoaded(RunExecutable(output, "NetLayout"), rid);
    }

    /// <summary>A single-file publish keeps the library in its subfolder beside the bundle, and loads it.</summary>
    [Fact]
    public void A_single_file_publish_loads_from_a_subfolder_beside_the_bundle()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("single-file");
        CommandResult publish = sdk.Once($"publish NetLayout -r {rid} single file", () =>
            sdk.Dotnet("publish", Layout, "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true", "-p:NetLayoutReference=native", "-o", output));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.Equal(new[] { LibraryPath("native", rid) }, EverywhereFiles(output));
        AssertLoaded(RunExecutable(output, "NetLayout"), rid);
    }

    /// <summary>
    /// NativeAOT reads no deps.json: the program finds the library in its subfolder only through
    /// the generated loader. Skipped on a machine without NativeAOT's platform linker.
    /// </summary>
    [Fact]
    public void NativeAOT_loads_from_a_subfolder_through_the_generated_loader()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("nativeaot");
        CommandResult publish = sdk.Once($"publish NetLayout -r {rid} NativeAOT", () =>
            sdk.Dotnet("publish", Layout, "-c", "Release", "-r", rid, "-p:PublishAot=true", "-p:NetLayoutReference=native", "-o", output));
        SkipWithoutNativeAotLinker(publish);
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.Equal(new[] { LibraryPath("native", rid) }, EverywhereFiles(output));
        AssertLoaded(RunExecutable(output, "NetLayout"), rid);
    }

    /// <summary>Skips when a NativeAOT publish failed for want of the platform linker (Visual Studio's on Windows, clang elsewhere).</summary>
    /// <param name="publish">The publish's result.</param>
    /// <exception cref="Exception">Xunit's skip exception.</exception>
    private static void SkipWithoutNativeAotLinker(CommandResult publish)
    {
        if (publish.ExitCode != 0 && (publish.Output.Contains("Platform linker", StringComparison.Ordinal) || publish.Output.Contains("nativeaot-prerequisites", StringComparison.Ordinal)))
        {
            Assert.Skip("this machine lacks NativeAOT's platform linker (https://aka.ms/nativeaot-prerequisites)");
        }
    }

    /// <summary>
    /// A folder without {rid} in a portable build of eight RIDs: win-x64 and win-arm64 (and the
    /// four Linux RIDs) give one file name, and the build stops with ASM0044 before anything
    /// overwrites anything.
    /// </summary>
    [Fact]
    public void Two_rids_with_one_file_name_in_one_folder_stop_the_build()
    {
        CommandResult build = sdk.Once("build NetLayout collision", () => sdk.Dotnet("build", Layout, "-c", "Release", "-p:NetLayoutReference=native", "-o", RunFolder("collision")));
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("ASM0044", build.StdOut);
        Assert.Contains("native/{rid}", build.StdOut);
    }

    /// <summary>A value that leaves the output folder, is absolute, holds a character no OS takes or an unknown placeholder stops with ASM0043.</summary>
    /// <param name="value">The value.</param>
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("native/{os}")]
    [InlineData("na|tive")]
    public void A_value_outside_the_output_or_malformed_is_refused(string value)
    {
        CommandResult build = sdk.Dotnet("build", Layout, "-c", "Release", $"-p:NetLayoutReference={value}", "-o", RunFolder("invalid"));
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("ASM0043", build.StdOut);
    }

    /// <summary>
    /// The loader is compiled where a layout puts libraries outside the app folder (a portable
    /// build's default included), not where they sit next to the app (a RID-specific build's
    /// default), and AsmGenerateNativeLibraryLoader=false leaves it out.
    /// </summary>
    [Fact]
    public void The_loader_is_compiled_where_the_layout_needs_it_and_can_be_turned_off()
    {
        string rid = HostRidOrSkip();
        string portable = RunFolder("loader-portable");
        string ridSpecific = RunFolder("loader-rid");
        string off = RunFolder("loader-off");
        Assert.Equal(0, sdk.Once("build NetLayout loader portable", () => sdk.Dotnet("build", Layout, "-c", "Release", "-o", portable)).ExitCode);
        Assert.Equal(0, sdk.Once($"build NetLayout loader -r {rid}", () => sdk.Dotnet("build", Layout, "-c", "Release", "-r", rid, "-o", ridSpecific)).ExitCode);
        Assert.Equal(0, sdk.Once("build NetLayout loader off", () => sdk.Dotnet("build", Layout, "-c", "Release", "-p:AsmGenerateNativeLibraryLoader=false", "-o", off)).ExitCode);
        Assert.True(HasLoader(Path.Combine(portable, "NetLayout.dll")), "no loader in a portable build");
        Assert.False(HasLoader(Path.Combine(ridSpecific, "NetLayout.dll")), "a loader where every library sits next to the app");
        Assert.False(HasLoader(Path.Combine(off, "NetLayout.dll")), "a loader although AsmGenerateNativeLibraryLoader=false");
        AssertLoaded(RunHere(off, "NetLayout.dll"), rid);
    }

    /// <summary>
    /// PrivateAssets="all" on the wrapper's reference stops the asm reference from flowing: the
    /// program gets the wrapper's copies but no consumer targets and no deps.json entry, and
    /// still loads the library, through the loader compiled into the wrapper.
    /// </summary>
    [Fact]
    public void A_private_asm_reference_loads_through_the_library_loader()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("private-reference");
        CommandResult build = sdk.Once("build NetLayoutThrough private", () => sdk.Dotnet("build", LayoutThrough, "-c", "Release", "-p:NetLayoutLibraryPrivate=all", ThroughOutput(output)));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Empty(DepsAssets(Path.Combine(output, "NetLayoutThrough.deps.json"), EverywhereLibrary));
        Assert.True(HasLoader(Path.Combine(output, "NetLayoutLibrary.dll")), "no loader in the wrapper library");
        AssertLoaded(RunHere(output, "NetLayoutThrough.dll"), rid);
    }

    /// <summary>
    /// A private asm reference WITH a layout: the program has no consumer targets (the reference
    /// does not flow), so it copies exactly what the wrapper hands on, and the files must arrive
    /// where the wrapper's layout and loader say. Found by the delivery QA matrix: before the
    /// wrapper delivered its asm files laid out itself, the program got
    /// runtimes/&lt;rid&gt;/native/ while the loader looked in native/&lt;rid&gt;/
    /// (DllNotFoundException on every platform; the default layout passed by coincidence).
    /// </summary>
    /// <param name="value">The wrapper reference's BinariesCopyPath.</param>
    /// <param name="folder">The folder it means.</param>
    [Theory]
    [InlineData("native/{rid}", "native/{rid}")]
    [InlineData("RidFolders", "{rid}")]
    public void A_private_asm_reference_with_a_layout_reaches_the_program(string value, string folder)
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("private-" + folder.Replace("{rid}", "rid").Replace('/', '-'));
        CommandResult build = sdk.Once($"build NetLayoutThrough private {value}", () =>
            sdk.Dotnet("build", LayoutThrough, "-c", "Release", "-p:NetLayoutLibraryPrivate=all", $"-p:NetLayoutLibraryReference={value}", ThroughOutput(output)));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath(folder, r)).Order(StringComparer.Ordinal), EverywhereFiles(output));
        Assert.Empty(DepsAssets(Path.Combine(output, "NetLayoutThrough.deps.json"), EverywhereLibrary));
        AssertLoaded(RunHere(output, "NetLayoutThrough.dll"), rid);
    }

    /// <summary>
    /// The same through a single-file publish. The program cannot mark the files
    /// AssetType=native, so the wrapper hands them on with ExcludeFromSingleFile: they stay
    /// beside the bundle (every RID's: nothing in the program filters them), instead of a
    /// NETSDK1236 that leaves a .dll neither bundled nor copied, and the wrapper's loader finds
    /// this machine's build there.
    /// </summary>
    [Fact]
    public void A_private_asm_reference_with_a_layout_survives_a_single_file_publish()
    {
        string rid = HostRidOrSkip();
        string output = RunFolder("private-single-file");
        CommandResult publish = sdk.Once($"publish NetLayoutThrough -r {rid} private single file", () =>
            sdk.Dotnet("publish", LayoutThrough, "-c", "Release", "-r", rid, "--self-contained", "-p:PublishSingleFile=true",
                "-p:NetLayoutLibraryPrivate=all", "-p:NetLayoutLibraryReference=native/{rid}", "-o", output));
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.DoesNotContain("NETSDK1236", publish.StdOut);
        Assert.Contains(LibraryPath("native/{rid}", rid), EverywhereFiles(output));
        AssertLoaded(RunExecutable(output, "NetLayoutThrough"), rid);
    }

    /// <summary>Packs the wrapper with PrivateAssets="all" into the local feed, once.</summary>
    /// <returns>The pack's result.</returns>
    private CommandResult PackPrivateWrapper() =>
        sdk.Once("pack NetLayoutLibrary private", () => sdk.Dotnet("pack", LayoutLibrary, "-c", "Release", "-p:NetLayoutLibraryPrivate=all"));

    /// <summary>
    /// dotnet pack of a .NET library with PrivateAssets="all" on its asm reference: every RID's
    /// library goes INTO its package under runtimes/&lt;rid&gt;/native/, with buildTransitive targets
    /// for BinariesCopyPath, and the package depends on neither the asm package nor the SDK.
    /// </summary>
    [Fact]
    public void A_private_asm_reference_is_packed_into_the_dotnet_package()
    {
        CommandResult pack = PackPrivateWrapper();
        Assert.True(pack.ExitCode == 0, pack.Tail());
        string nupkg = Path.Combine(Repo.Artifacts, "packages", "Test.NetLayoutLibrary.1.0.0.nupkg");
        SortedSet<string> entries = SdkUnderTest.PackageEntries(nupkg);
        Assert.Contains("lib/net10.0/NetLayoutLibrary.dll", entries);
        Assert.Contains("buildTransitive/net10.0/Test.NetLayoutLibrary.targets", entries);
        foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
        {
            Assert.Contains($"runtimes/{rid}/native/{CrossPlatformTests.LibraryFile(rid, "everywhere")}", entries);
        }

        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        using var reader = new StreamReader(archive.GetEntry("Test.NetLayoutLibrary.nuspec")!.Open());
        string nuspec = reader.ReadToEnd();
        Assert.DoesNotContain("Test.Everywhere", nuspec);
        Assert.DoesNotContain("AsmFoundation.Sdk", nuspec);
    }

    /// <summary>
    /// A program that gets the wrapper as a package (nothing of AsmSdk in its graph) moves the
    /// packaged binaries with BinariesCopyPath on its PackageReference, by the package's own
    /// targets, and loads this machine's build from there.
    /// </summary>
    /// <param name="value">The value as written.</param>
    /// <param name="folder">The folder it means.</param>
    [Theory]
    [InlineData("native/{rid}", "native/{rid}")]
    [InlineData("RidFolders", "{rid}")]
    public void A_package_consumer_moves_the_packaged_binaries(string value, string folder)
    {
        string rid = HostRidOrSkip();
        Assert.Equal(0, PackPrivateWrapper().ExitCode);
        string output = RunFolder("package-" + folder.Replace("{rid}", "rid").Replace('/', '-'));
        CommandResult build = sdk.Once($"build NetLayoutPackage {value}", () => sdk.Dotnet("build", LayoutPackage, "-c", "Release", $"-p:NetLayoutPackageReference={value}", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("asmfoundation.sdk", File.ReadAllText(Repo.TestProjectPath("NetLayoutPackage", "obj", "project.assets.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath(folder, r)).Order(StringComparer.Ordinal), EverywhereFiles(output));
        AssertLoaded(RunHere(output, "NetLayoutPackage.dll"), rid);
    }

    /// <summary>A package consumer built for this RID gets its RID's binary in the subfolder, and loads it.</summary>
    [Fact]
    public void A_package_consumer_rid_specific_build_loads_from_a_subfolder()
    {
        string rid = HostRidOrSkip();
        Assert.Equal(0, PackPrivateWrapper().ExitCode);
        string output = RunFolder("package-rid-specific");
        CommandResult build = sdk.Once($"build NetLayoutPackage -r {rid}", () => sdk.Dotnet("build", LayoutPackage, "-c", "Release", "-r", rid, "-p:NetLayoutPackageReference=native", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Equal(new[] { LibraryPath("native", rid) }, EverywhereFiles(output));
        AssertLoaded(RunHere(output, "NetLayoutPackage.dll"), rid);
    }

    /// <summary>A package consumer's NativeAOT publish loads the packaged binary from its subfolder through the loader the package's targets generate.</summary>
    [Fact]
    public void A_package_consumer_NativeAOT_loads_through_the_package_loader()
    {
        string rid = HostRidOrSkip();
        Assert.Equal(0, PackPrivateWrapper().ExitCode);
        string output = RunFolder("package-nativeaot");
        CommandResult publish = sdk.Once($"publish NetLayoutPackage -r {rid} NativeAOT", () =>
            sdk.Dotnet("publish", LayoutPackage, "-c", "Release", "-r", rid, "-p:PublishAot=true", "-p:NetLayoutPackageReference=native", "-o", output));
        SkipWithoutNativeAotLinker(publish);
        Assert.True(publish.ExitCode == 0, publish.Tail());
        Assert.Equal(new[] { LibraryPath("native", rid) }, EverywhereFiles(output));
        AssertLoaded(RunExecutable(output, "NetLayoutPackage"), rid);
    }

    /// <summary>
    /// The asm package itself (Test.Everywhere) carries the same targets: a .NET consumer moves its
    /// binaries with BinariesCopyPath on the PackageReference, without AsmSdk in its graph.
    /// </summary>
    [Fact]
    public void An_asm_package_consumer_moves_the_binaries()
    {
        string rid = HostRidOrSkip();
        CommandResult pack = PackEverywhere(sdk);
        Assert.True(pack.ExitCode == 0, pack.Tail());
        Assert.Contains("buildTransitive/netstandard1.0/Test.Everywhere.targets", SdkUnderTest.PackageEntries(Path.Combine(Repo.Artifacts, "packages", "Test.Everywhere.1.0.0.nupkg")));
        string output = RunFolder("asm-package");
        CommandResult build = sdk.Once("build NetAsmPackage RidFolders", () => sdk.Dotnet("build", AsmPackage, "-c", "Release", "-p:NetAsmPackageReference=RidFolders", "-o", output));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.DoesNotContain("asmfoundation.sdk", File.ReadAllText(Repo.TestProjectPath("NetAsmPackage", "obj", "project.assets.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CrossPlatformTests.PlatformCode.Keys.Select(r => LibraryPath("{rid}", r)).Order(StringComparer.Ordinal), EverywhereFiles(output));
        AssertLoaded(RunHere(output, "NetAsmPackage.dll"), rid);
    }
}
