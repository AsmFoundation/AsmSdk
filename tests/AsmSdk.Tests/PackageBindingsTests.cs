using System.IO.Compression;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The C# bindings of asm packages, end to end (phase 4 of catalog asmsdk/plans/csharp-bindings.md,
/// stage 3): a package of an asm library with PROTO or STRUCT lines carries its baseline, written
/// at pack time (contentFiles/cs/net7.0/AsmBindings/&lt;library&gt;.cs.pp, every layout under #if,
/// the namespace and the modifiers as NuGet tokens), its API file and the bindings generator, with
/// targets of its own and no dependency on AsmFoundation.Sdk. A C# consumer of the package calls the
/// library through the written baseline, shapes it in its project file, and gets the newest
/// generator once its code names the attribute.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// The packages (tests/projects/BindingsPackage, BindingsPackageB) are packed into the local feed
/// once per run, with every RID's library, so each CI machine runs its own. BindingsPackageB is
/// packed claiming the generator version 9.9.9 (_AsmPackBindingsGeneratorVersion): the same file,
/// so behavior is the same, but the shared pick must choose its copy over the SDK's and the other
/// package's. The fixture clears the feed's test packages and the isolated NuGet cache before each
/// run, so nothing restored earlier can stand in for a package packed now.
/// </remarks>
public sealed class PackageBindingsTests(SdkUnderTest sdk)
{
    /// <summary>The package with three functions and a struct.</summary>
    private static readonly string PackageA = Path.Combine(Repo.Artifacts, "packages", "Test.BindingsPackage.1.0.0.nupkg");

    /// <summary>
    /// The package carries the bindings: the baseline as a Compile content file (in the nuspec's
    /// contentFiles, which is what makes NuGet compile it), the API file and the generator under
    /// build/bindings/, the targets in build/ and buildTransitive/, and no dependency on
    /// AsmFoundation.Sdk. The baseline holds every layout and never says the switch's word; the
    /// shared targets in the package's targets are the SDK's, character for character (MSBuild keeps
    /// the last definition of a name, so every copy must be the same).
    /// </summary>
    [Fact]
    public void The_package_carries_the_bindings_and_no_sdk()
    {
        PackPackages();
        SortedSet<string> entries = SdkUnderTest.PackageEntries(PackageA);
        Assert.Contains("contentFiles/cs/net7.0/AsmBindings/bindpkg.cs.pp", entries);
        Assert.Contains("build/bindings/bindpkg.api.json", entries);
        Assert.Contains("build/bindings/AsmFoundation.Sdk.Bindings.dll", entries);
        Assert.Contains("build/netstandard1.0/Test.BindingsPackage.targets", entries);
        Assert.Contains("buildTransitive/netstandard1.0/Test.BindingsPackage.targets", entries);

        string nuspec = EntryText(PackageA, "Test.BindingsPackage.nuspec");
        Assert.Contains("<files include=\"cs/net7.0/AsmBindings/bindpkg.cs.pp\" buildAction=\"Compile\" />", nuspec);
        Assert.DoesNotContain("AsmFoundation.Sdk", nuspec, StringComparison.OrdinalIgnoreCase);

        string baseline = EntryText(PackageA, "contentFiles/cs/net7.0/AsmBindings/bindpkg.cs.pp");
        Assert.DoesNotContain("AsmLibrary", baseline);
        Assert.Contains("#define ASMBINDINGS_NESTING_$asmbindings_Test_BindingsPackage_nesting$", baseline);
        foreach (string layout in new[] { "Files", "Folders", "Flat" })
        {
            Assert.Contains($"ASMBINDINGS_NESTING_{layout} && ASMBINDINGS_KIND_Static", baseline);
            Assert.Contains($"ASMBINDINGS_NESTING_{layout} && ASMBINDINGS_KIND_Instance", baseline);
        }

        Assert.Contains("$asmbindings_Test_BindingsPackage_typeaccess$ unsafe struct Point", baseline);

        string packageTargets = EntryText(PackageA, "build/netstandard1.0/Test.BindingsPackage.targets");
        string sdkTargets = File.ReadAllText(Path.Combine(Repo.Root, "src", "AsmFoundation.Sdk", "buildTransitive", "AsmFoundation.Sdk.targets"));
        Assert.Equal(SharedTargets(sdkTargets), SharedTargets(packageTargets));
    }

    /// <summary>
    /// tests/projects/NetBindingsPackage calls the package's library through the written baseline
    /// (Bindpkg.Add, Bindpkg.Simd.Sum's span overload, Bindpkg.Geometry with the struct Point),
    /// all internal; no generator ran, and AsmFoundation.Sdk is nowhere in its restore graph.
    /// </summary>
    [Fact]
    public void A_package_consumer_calls_the_library_through_the_written_baseline()
    {
        HostRidOrSkip();
        PackPackages();
        CommandResult build = sdk.Once("build NetBindingsPackage", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsPackage", "NetBindingsPackage.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        // A package in the graph is a library "<id>/<version>"; the bare name would also match
        // the package's own file build/bindings/AsmFoundation.Sdk.Bindings.dll (measured).
        Assert.DoesNotContain("\"AsmFoundation.Sdk/", File.ReadAllText(Repo.TestProjectPath("NetBindingsPackage", "obj", "project.assets.json")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["add_ints 42", "sum_bytes 10", "point_sum 42", "internal True", "attribute False"],
            Run("NetBindingsPackage"));
    }

    /// <summary>
    /// tests/projects/NetBindingsPackageShapes shapes the package's baseline in its project file
    /// alone: the properties (instance classes, public, sealed, no span overloads) and the item
    /// (the namespace Shapes.Pkg, a class per folder). The package's targets fill the tokens from
    /// them, the item first.
    /// </summary>
    [Fact]
    public void A_package_consumer_shapes_the_baseline_in_its_project_file()
    {
        HostRidOrSkip();
        PackPackages();
        CommandResult build = sdk.Once("build NetBindingsPackageShapes", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsPackageShapes", "NetBindingsPackageShapes.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Equal(
            ["Api.AddInts 42 Api.PointSum 42", "Simd.SumBytes 10 overloads 1",
             "public True sealed True static False instance True virtual False", "Point public True"],
            Run("NetBindingsPackageShapes"));
    }

    /// <summary>
    /// A changed setting rewrites the baseline in the next build of the same configuration: NuGet
    /// keeps a preprocessed content file per set of token values (obj/.../NuGet/&lt;hash&gt;/), so
    /// AsmBindingsNesting=Flat leaves no class Bindpkg.Add for Program.cs to call (CS0234), and the
    /// default compiles again afterwards.
    /// </summary>
    [Fact]
    public void A_changed_setting_rewrites_the_package_baseline()
    {
        HostRidOrSkip();
        PackPackages();
        string project = Repo.TestProjectPath("NetBindingsPackage", "NetBindingsPackage.csproj");
        CommandResult first = sdk.Dotnet("build", project, "-c", "Rewrite");
        Assert.True(first.ExitCode == 0, first.Tail());
        CommandResult flat = sdk.Dotnet("build", project, "-c", "Rewrite", "-p:AsmBindingsNesting=Flat");
        Assert.True(flat.ExitCode != 0, flat.Tail());
        Assert.Contains("error CS0234: The type or namespace name 'Add' does not exist in the namespace 'Bindpkg'", flat.StdOut);
        CommandResult again = sdk.Dotnet("build", project, "-c", "Rewrite");
        Assert.True(again.ExitCode == 0, again.Tail());
    }

    /// <summary>
    /// tests/projects/NetBindingsMixed names the attribute, with bindings from three places: an asm
    /// ProjectReference (ApiProto, through the SDK's consumer targets) and both packages. One
    /// generator runs, the newest candidate (BindingsPackageB's copy, packed claiming 9.9.9): the
    /// compiler is handed exactly one bindings analyzer, that one. It writes every library's
    /// baseline (bindpkg's reshaped by DefaultNesting = Flat, read from the package's API file)
    /// and the copy Native; the written baselines stepped aside (two of each type would be CS0101).
    /// </summary>
    [Fact]
    public void The_attribute_runs_one_generator_for_every_library()
    {
        HostRidOrSkip();
        PackPackages();
        CommandResult build = sdk.Once("build NetBindingsMixed", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsMixed", "NetBindingsMixed.csproj"), "-c", "Release", "-v:n"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string[] analyzers = Regex.Matches(build.StdOut, @"/analyzer:(\S*AsmFoundation\.Sdk\.Bindings\.dll)")
            .Select(m => m.Groups[1].Value.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string analyzer = Assert.Single(analyzers);
        Assert.Contains("/test.bindingspackageb/1.0.0/build/bindings/", analyzer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["Apiproto.Add.AddInts 42", "Bindpkg.Api.AddInts 42", "Bindpkg.Api.SumBytes 10", "Bindpkgb.Mul.MulInts 42", "Native.MulInts 42", "attribute True"],
            Run("NetBindingsMixed"));
    }

    /// <summary>
    /// A wrong setting for a package's baseline stops the build with the SDK's code and words,
    /// checked by the package's own targets (tests/projects/NetBindingsPackageErrors, no SDK in the
    /// graph): a property's value, an item's value, options C# cannot follow together (Virtual on
    /// the default static classes: the defaults count), a namespace C# cannot have, and an item for
    /// a library the project has no bindings for (the shared check).
    /// </summary>
    /// <param name="bindingsCase">The case (the project file's condition).</param>
    /// <param name="code">The expected code.</param>
    /// <param name="message">A part of the expected message.</param>
    [Theory]
    [InlineData("badproperty", "ASM0146", "AsmBindingsNesting is 'Flatt', which is not one of Files, Folders, Flat")]
    [InlineData("baditem", "ASM0146", "The AsmBindings item of bindpkg: Visibility is 'Publik', which is not one of Internal, Public, ProtectedInternal")]
    [InlineData("badshape", "ASM0146", "bindpkg's baseline: Virtual needs instance members (a static member cannot be virtual): set ClassKind to Instance")]
    [InlineData("badnamespace", "ASM0146", "bindpkg's baseline namespace 'My-App' is not a C# namespace name")]
    [InlineData("unknownitem", "ASM0140", "<AsmBindings Include=\"nolib\" /> names no asm library this project references with PROTO or STRUCT lines; it references bindpkg")]
    public void A_wrong_package_setting_stops_the_build_with_its_code(string bindingsCase, string code, string message)
    {
        PackPackages();
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsPackageErrors", "NetBindingsPackageErrors.csproj"), "-c", $"Bindings{bindingsCase}", $"-p:BindingsCase={bindingsCase}");
        Assert.True(build.ExitCode != 0, $"case {bindingsCase} built: {build.Tail()}");
        Assert.Contains($"error {code}: {message}", build.StdOut, StringComparison.Ordinal);
    }

    /// <summary>AsmGenerateBindings=false takes the package's baseline out of the compilation: Program.cs then names a namespace that does not exist.</summary>
    [Fact]
    public void AsmGenerateBindings_false_removes_the_package_baseline()
    {
        PackPackages();
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsPackage", "NetBindingsPackage.csproj"), "-c", "NoBindings", "-p:AsmGenerateBindings=false");
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("error CS0103: The name 'Bindpkg' does not exist in the current context", build.StdOut);
    }

    /// <summary>Packs both test packages into the local feed, once per run (B claiming the generator version 9.9.9).</summary>
    private void PackPackages()
    {
        CommandResult a = sdk.Once("pack BindingsPackage into the feed", () => sdk.Dotnet("pack", Repo.TestProject("BindingsPackage"), "-c", "Release"));
        Assert.True(a.ExitCode == 0, a.Tail());
        CommandResult b = sdk.Once("pack BindingsPackageB into the feed", () =>
            sdk.Dotnet("pack", Repo.TestProject("BindingsPackageB"), "-c", "Release", "-p:_AsmPackBindingsGeneratorVersion=9.9.9"));
        Assert.True(b.ExitCode == 0, b.Tail());
    }

    /// <summary>Runs a built test program of tests/projects (Release, net10.0) and returns its output lines.</summary>
    /// <param name="name">The project's name.</param>
    /// <returns>The lines, trimmed, without empty ones.</returns>
    private static string[] Run(string name)
    {
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath(name, "bin", "Release", "net10.0", name + ".dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        return run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>One file of a package, as text.</summary>
    /// <param name="nupkg">The .nupkg file.</param>
    /// <param name="entry">The entry's name, with forward slashes.</param>
    /// <returns>Its text.</returns>
    /// <exception cref="InvalidOperationException">The package has no such entry.</exception>
    private static string EntryText(string nupkg, string entry)
    {
        using ZipArchive archive = ZipFile.OpenRead(nupkg);
        ZipArchiveEntry found = archive.GetEntry(entry) ?? throw new InvalidOperationException($"{Path.GetFileName(nupkg)} has no {entry}");
        using var reader = new StreamReader(found.Open());
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The shared targets of a targets file: from the comment that opens them to the end of
    /// _AsmPickBindingsGenerator, with "\n" line ends (a Windows checkout may hold CRLF).
    /// </summary>
    /// <param name="targets">The targets file's text.</param>
    /// <returns>The shared targets' text.</returns>
    private static string SharedTargets(string targets)
    {
        string text = targets.Replace("\r\n", "\n");
        int start = text.IndexOf("  <!--\n    The C# bindings' shared targets.", StringComparison.Ordinal);
        Assert.True(start >= 0, "no shared bindings targets");
        int pick = text.IndexOf("<Target Name=\"_AsmPickBindingsGenerator\"", start, StringComparison.Ordinal);
        Assert.True(pick >= 0, "no _AsmPickBindingsGenerator");
        int end = text.IndexOf("</Target>\n", pick, StringComparison.Ordinal) + "</Target>\n".Length;
        return text[start..end];
    }

    /// <summary>Skips on a machine whose RID AsmSdk does not build: nothing could run there.</summary>
    /// <exception cref="Exception">Xunit's skip exception.</exception>
    private static void HostRidOrSkip()
    {
        if (CrossPlatformTests.HostBuildRid() is null)
        {
            Assert.Skip($"AsmSdk builds no library for this machine ({Repo.HostRid})");
        }
    }
}
