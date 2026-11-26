using System.Text.Json;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// What IDEs (Rider, Visual Studio, VS Code) see of an .asmproj: every file of the project
/// folder as an item (the sources as Compile items, everything else as None, as in a .csproj),
/// the inputs and outputs of their fast up-to-date checks, and design-time builds that never
/// run the tools; and of the C# bindings of a project that references one: what an IDE's C#
/// engine compiles (the written baseline, the generator, a package's baseline).
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// These are checked through MSBuild itself (what an IDE asks MSBuild for), since no IDE runs
/// here. The design-time builds replay the targets and properties Rider's MSBuild host uses, and
/// for the C# bindings also Visual Studio's (DesignTimeCompilation says how the two differ).
/// </remarks>
public sealed class IdeIntegrationTests(SdkUnderTest sdk)
{
    /// <summary>A tool command line at normal verbosity (assemble, link or archive): IncrementalBuildTests' pattern.</summary>
    private static readonly Regex ToolRun = IncrementalBuildTests.ToolRun;

    /// <summary>
    /// Evaluates a test project for one target framework and returns the identities of the
    /// requested item types, the way an IDE reads a project.
    /// </summary>
    /// <param name="project">The test project's name.</param>
    /// <param name="framework">The target framework to evaluate.</param>
    /// <param name="itemTypes">The item types to read.</param>
    /// <returns>Per item type, its items' identities (paths relative to the project folder for globbed files).</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The evaluation failed (the assertion reports MSBuild's output).</exception>
    private Dictionary<string, string[]> Items(string project, string framework, params string[] itemTypes)
    {
        // Restore first: evaluation needs the SDK resolved from the isolated NuGet cache.
        CommandResult restore = sdk.Once($"restore {project}", () => sdk.Dotnet("restore", Repo.TestProject(project)));
        Assert.True(restore.ExitCode == 0, restore.Tail());
        CommandResult result = sdk.Dotnet(["msbuild", Repo.TestProject(project), $"-p:TargetFramework={framework}",
            .. itemTypes.Select(t => $"-getItem:{t}")]);
        Assert.True(result.ExitCode == 0, result.Tail());
        using JsonDocument json = JsonDocument.Parse(result.StdOut);
        JsonElement items = json.RootElement.GetProperty("Items");
        return itemTypes.ToDictionary(type => type, type => items.TryGetProperty(type, out JsonElement list)
            ? list.EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/')).ToArray()
            : []);
    }

    /// <summary>
    /// An .asmproj carries the generic project type GUID of SDK-style projects (Visual Studio's
    /// Common Project System), as DefaultProjectTypeGuid: "dotnet sln add" reads it and writes it
    /// into the solution, where 0.3.0 had none and the CLI printed an unknown project type and
    /// added nothing (with exit code 0). The PlugAndPlay scenario runs dotnet sln add itself.
    /// </summary>
    [Fact]
    public void An_asmproj_names_its_project_type_for_solutions()
    {
        CommandResult restore = sdk.Once("restore Exports", () => sdk.Dotnet("restore", Repo.TestProject("Exports")));
        Assert.True(restore.ExitCode == 0, restore.Tail());
        CommandResult result = sdk.Dotnet("msbuild", Repo.TestProject("Exports"), "-getProperty:DefaultProjectTypeGuid");
        Assert.True(result.ExitCode == 0, result.Tail());
        Assert.Equal("{13B669BE-BB05-4DDF-9536-439F39A36129}", result.StdOut.Trim());
    }

    /// <summary>
    /// The sources are Compile items and the include files None items, the item types every
    /// IDE shows on its own, so the SDK declares no item type of its own any more: 0.3.0's
    /// AsmSource and AsmInclude had to be AvailableItemName items, or Rider showed an .asmproj
    /// empty. (Measured before the switch, 2026-11-23: Rider's engine never analyzes a .asm or .s
    /// Compile item as C#, and Visual Studio's C# language service needs the CSharp and
    /// Managed capabilities, which <see cref="An_asmproj_is_no_csharp_project_for_the_IDEs"/>
    /// checks are absent.)
    /// </summary>
    [Fact]
    public void The_asm_items_are_dotnets_own_item_types()
    {
        string[] available = Items("Exports", "libc", "AvailableItemName")["AvailableItemName"];
        Assert.DoesNotContain("AsmSource", available);
        Assert.DoesNotContain("AsmInclude", available);
        Assert.Equal(["exports.asm"], Items("Exports", "libc", "Compile")["Compile"]);
    }

    /// <summary>
    /// An .asmproj has none of the capabilities that make Visual Studio attach its C# language
    /// service (CSharp, Managed, LanguageService) and no Language, so its Compile items are never
    /// handed to the C# compiler there; a .csproj has all three.
    /// </summary>
    [Fact]
    public void An_asmproj_is_no_csharp_project_for_the_IDEs()
    {
        string[] capabilities = Items("Exports", "libc", "ProjectCapability")["ProjectCapability"];
        Assert.DoesNotContain("CSharp", capabilities);
        Assert.DoesNotContain("Managed", capabilities);
        Assert.DoesNotContain("LanguageService", capabilities);
    }

    /// <summary>
    /// Every file of the project folder is exactly one item, as in a .csproj: sources are
    /// Compile items, everything else None, include files (here include/libconly.inc) and the
    /// linker version script alike. The project file and bin/ and obj/ are no items, even after
    /// a build created them.
    /// </summary>
    [Fact]
    public void Every_file_of_the_project_folder_is_one_item()
    {
        CommandResult build = sdk.BuildTestProject("Exports");
        Assert.True(build.ExitCode == 0, build.Tail());
        Dictionary<string, string[]> items = Items("Exports", "libc", "Compile", "None");
        Assert.Equal(["exports.asm"], items["Compile"]);
        Assert.Equal(["exports.map"], items["None"]);

        items = Items("LibcPackage", "libc", "Compile", "None");
        Assert.Equal(["libconly.asm"], items["Compile"]);
        Assert.Equal(["include/libconly.inc"], items["None"]);
    }

    /// <summary>
    /// The fast up-to-date checks of Visual Studio and Rider see what a build depends on (the
    /// sources, includes and the exports file) and what it produces, so editing a .asm file
    /// makes them call MSBuild.
    /// </summary>
    [Fact]
    public void The_up_to_date_check_knows_the_inputs_and_the_output()
    {
        Dictionary<string, string[]> items = Items("Exports", "libc", "UpToDateCheckInput", "UpToDateCheckBuilt");
        Assert.Equal(["exports.asm", "exports.map"], items["UpToDateCheckInput"].Order(StringComparer.Ordinal));
        string built = Assert.Single(items["UpToDateCheckBuilt"]);
        Assert.EndsWith("bin/Debug/libc/linux-x64/libexports.so", built);
    }

    /// <summary>
    /// The fast up-to-date checks also see the inputs that are no sources, as .NET names them
    /// (tests/projects/DependencyInputs): an include file from outside the project listed as a
    /// None item, and a CustomAdditionalCompileInputs item; not a source taken out with Compile
    /// Remove, nor a None item that is no input (readme.txt).
    /// </summary>
    [Fact]
    public void The_up_to_date_check_knows_include_files_and_extra_inputs()
    {
        Dictionary<string, string[]> items = Items("DependencyInputs", "freestanding", "Compile", "UpToDateCheckInput");
        Assert.Equal(["deps.asm"], items["Compile"]);
        Assert.Equal(["../SharedIncludes/outside.inc", "blob.bin", "deps.asm"], items["UpToDateCheckInput"].Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A design-time build, as Rider runs one on every load (Compile and CoreCompile with
    /// DesignTimeBuild and SkipCompilerExecution), runs no assembler or linker and writes no
    /// object. The control, the same targets without those properties, does run them: the probe
    /// would catch a design-time build that assembled.
    /// </summary>
    [Fact]
    public void A_design_time_build_runs_no_tool()
    {
        string[] targets = ["-t:ResolveReferences;ResolveProjectReferences;Compile;CoreCompile", "-p:TargetFramework=libc", "-v:n"];
        string probeObjects = Repo.TestProjectPath("Exports", "obj", "DesignTimeProbe");
        string controlObjects = Repo.TestProjectPath("Exports", "obj", "DesignTimeControl");
        CommandResult restore = sdk.Once("restore Exports", () => sdk.Dotnet("restore", Repo.TestProject("Exports")));
        Assert.True(restore.ExitCode == 0, restore.Tail());
        try
        {
            // Fresh configurations: obj/<Configuration>/ starts empty, so any object in it was
            // written by this run.
            CommandResult designTime = sdk.Dotnet(["msbuild", Repo.TestProject("Exports"), .. targets,
                "-p:Configuration=DesignTimeProbe", "-p:DesignTimeBuild=true", "-p:JetBrainsDesignTimeBuild=true",
                "-p:SkipCompilerExecution=true", "-p:ProvideCommandLineArgs=true", "-p:BuildingProject=false"]);
            Assert.True(designTime.ExitCode == 0, designTime.Tail());
            Assert.Empty(ToolRun.Matches(designTime.StdOut).Select(m => m.Value));
            Assert.False(Directory.Exists(Path.Combine(probeObjects, "libc", "linux-x64", "asm")), "a design-time build wrote asm/ outputs");

            CommandResult control = sdk.Dotnet(["msbuild", Repo.TestProject("Exports"), .. targets, "-p:Configuration=DesignTimeControl"]);
            Assert.True(control.ExitCode == 0, control.Tail());
            Assert.NotEmpty(ToolRun.Matches(control.StdOut).Select(m => m.Value));
            Assert.True(File.Exists(Path.Combine(controlObjects, "libc", "linux-x64", "asm", "obj", "exports.o")), "the control wrote no object");
        }
        finally
        {
            foreach (string folder in new[]
                     {
                         probeObjects, controlObjects, Repo.TestProjectPath("Exports", "bin", "DesignTimeProbe"),
                         Repo.TestProjectPath("Exports", "bin", "DesignTimeControl"),
                     })
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// The C# bindings in an IDE, through what each IDE's C# engine is handed by a design-time
    /// build (references not built): Rider's the project's items, Visual Studio's the compiler's
    /// command line (<see cref="DesignTimeCompilation"/>). After one real build of the asm library,
    /// the written baseline (obj/.../AsmBindings/Apiproto.g.cs) is one of the sources in both, so
    /// IntelliSense knows Apiproto.Add.AddInts, and no generator is loaded. Before the library's
    /// first build there is no API file, so no baseline: the IDE knows the bindings from the first
    /// build on (the documented limit; a design-time build never builds a reference).
    /// </summary>
    /// <remarks>
    /// The fresh replay also checks that it sees the app's own Program.cs, in both IDEs: an empty
    /// answer (what the first draft of this test got from Rider's replay of an up-to-date project,
    /// whose CoreCompile is skipped) would otherwise pass the "no baseline" check for the wrong
    /// reason.
    /// </remarks>
    [Fact]
    public void An_ide_compiles_the_written_baseline_from_the_first_build_on()
    {
        string project = Repo.TestProjectPath("NetBindings", "NetBindings.csproj");
        string[] configurations = ["DesignTimeFresh", "DesignTimeBuilt"];
        try
        {
            IdeCompilation fresh = DesignTimeCompilation(project, "DesignTimeFresh");
            Assert.Contains(fresh.Compile, source => source.EndsWith("/NetBindings/Program.cs", StringComparison.Ordinal));
            Assert.Contains(fresh.CommandLine, argument => argument.EndsWith("Program.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(fresh.Compile, source => source.EndsWith("/AsmBindings/Apiproto.g.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(fresh.CommandLine, argument => argument.EndsWith("/AsmBindings/Apiproto.g.cs", StringComparison.Ordinal));

            CommandResult build = sdk.Dotnet("build", project, "-c", "DesignTimeBuilt");
            Assert.True(build.ExitCode == 0, build.Tail());
            IdeCompilation built = DesignTimeCompilation(project, "DesignTimeBuilt");
            Assert.Contains(built.Compile, source => source.EndsWith("/obj/DesignTimeBuilt/net10.0/AsmBindings/Apiproto.g.cs", StringComparison.Ordinal));
            Assert.Contains(built.CommandLine, argument => argument.EndsWith("/obj/DesignTimeBuilt/net10.0/AsmBindings/Apiproto.g.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(built.Analyzers, analyzer => analyzer.EndsWith("/AsmFoundation.Sdk.Bindings.dll", StringComparison.Ordinal));
            Assert.DoesNotContain(built.CommandLine, argument => argument.Contains("AsmFoundation.Sdk.Bindings.dll", StringComparison.Ordinal));
        }
        finally
        {
            DeleteConfigurations(["NetBindings", "ApiProto"], configurations);
        }
    }

    /// <summary>
    /// With the attribute in the code (tests/projects/NetBindingsOptions), the design-time build
    /// hands the IDE's C# engine the bindings generator as an analyzer and the library's API file
    /// as an additional file, and no written baseline: the IDE runs the generator as the build
    /// does. Rider's items and Visual Studio's command line alike.
    /// </summary>
    [Fact]
    public void An_ide_runs_the_generator_when_the_code_names_the_attribute()
    {
        string project = Repo.TestProjectPath("NetBindingsOptions", "NetBindingsOptions.csproj");
        try
        {
            CommandResult build = sdk.Dotnet("build", project, "-c", "DesignTimeGenerator");
            Assert.True(build.ExitCode == 0, build.Tail());
            IdeCompilation compilation = DesignTimeCompilation(project, "DesignTimeGenerator");
            Assert.Contains(compilation.Analyzers, analyzer => analyzer.EndsWith("/AsmFoundation.Sdk.Bindings.dll", StringComparison.Ordinal));
            Assert.Contains(compilation.AdditionalFiles, file => file.EndsWith("/apiproto.api.json", StringComparison.Ordinal));
            Assert.DoesNotContain(compilation.Compile, source => source.Contains("/AsmBindings/", StringComparison.Ordinal));
            Assert.Contains(compilation.CommandLine, argument => argument.StartsWith("/analyzer:", StringComparison.Ordinal) && argument.EndsWith("/AsmFoundation.Sdk.Bindings.dll", StringComparison.Ordinal));
            Assert.Contains(compilation.CommandLine, argument => argument.StartsWith("/additionalfile:", StringComparison.Ordinal) && argument.EndsWith("/apiproto.api.json", StringComparison.Ordinal));
            Assert.DoesNotContain(compilation.CommandLine, argument => argument.Contains("/AsmBindings/", StringComparison.Ordinal));
        }
        finally
        {
            DeleteConfigurations(["NetBindingsOptions", "ApiProto"], ["DesignTimeGenerator"]);
        }
    }

    /// <summary>
    /// A package's baseline needs no build: after the restore alone, a design-time build of a
    /// package consumer (tests/projects/NetBindingsPackage) hands the IDE's C# engine the baseline
    /// NuGet preprocessed (obj/.../NuGet/&lt;hash&gt;/Test.BindingsPackage/1.0.0/AsmBindings/bindpkg.cs),
    /// so IntelliSense knows Bindpkg.Add.AddInts as soon as the package is restored.
    /// </summary>
    [Fact]
    public void An_ide_compiles_a_packages_baseline_without_a_build()
    {
        PackageBindingsTests.PackPackages(sdk);
        string project = Repo.TestProjectPath("NetBindingsPackage", "NetBindingsPackage.csproj");
        try
        {
            IdeCompilation compilation = DesignTimeCompilation(project, "DesignTimePackage");
            Assert.Contains(compilation.Compile, source => source.EndsWith("/Test.BindingsPackage/1.0.0/AsmBindings/bindpkg.cs", StringComparison.Ordinal));
            Assert.Contains(compilation.CommandLine, argument => argument.EndsWith("/Test.BindingsPackage/1.0.0/AsmBindings/bindpkg.cs", StringComparison.Ordinal));
        }
        finally
        {
            DeleteConfigurations(["NetBindingsPackage"], ["DesignTimePackage"]);
        }
    }

    /// <summary>
    /// What the C# engines of the two IDEs are handed by a design-time build of a C# project.
    /// Rider's reads the project's items as the design-time targets leave them; Visual Studio's
    /// reads the compiler's command line, which CoreCompile builds from those same items.
    /// </summary>
    /// <param name="Compile">Rider's sources (Compile items): full paths, forward slashes.</param>
    /// <param name="Analyzers">Rider's analyzers and generators (Analyzer items): full paths, forward slashes.</param>
    /// <param name="AdditionalFiles">Rider's additional files (AdditionalFiles items): full paths, forward slashes.</param>
    /// <param name="CommandLine">Visual Studio's compiler arguments (CscCommandLineArgs), forward slashes: the sources as paths (the project's own relative, written ones absolute), the options as /name:value.</param>
    private sealed record IdeCompilation(string[] Compile, string[] Analyzers, string[] AdditionalFiles, string[] CommandLine);

    /// <summary>
    /// Runs the design-time builds the two IDEs run when they load a C# project (the reference
    /// resolution and the compile targets with DesignTimeBuild, SkipCompilerExecution,
    /// ProvideCommandLineArgs and BuildingProject=false, so no reference is built and the compiler
    /// does not run), Rider's and Visual Studio's, and returns what each hands its C# engine.
    /// </summary>
    /// <param name="project">The project file.</param>
    /// <param name="configuration">A configuration of the test's own, so nothing another test built is reused unless the test built it.</param>
    /// <returns>Rider's items and Visual Studio's command line.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The restore or a design-time build failed (the assertion carries MSBuild's output).</exception>
    /// <remarks>
    /// Why two replays, measured 2026-11-26 on NetBindings after a real build: Rider's (its
    /// JetBrainsDesignTimeBuild marker, the properties its logs showed) skips CoreCompile once the
    /// last build's outputs are up to date, so it has no command line at all (0 arguments), and
    /// Rider needs none: it reads the items, which held the baseline. Visual Studio sets
    /// BuildingInsideVisualStudio, which makes CoreCompile run every time
    /// (_ComputeNonExistentFileProperty in Microsoft.Common.CurrentVersion.targets), so its command
    /// line is always there. That property also strips the configuration a reference would inherit
    /// (ShouldUnsetParentConfigurationAndPlatform) for the solution's configuration map to set it;
    /// with no solution here, ShouldUnsetParentConfigurationAndPlatform=false keeps the inherited
    /// one, which is what a solution maps it to. Without it the asm reference was asked for the API
    /// file of its Debug build, not this configuration's.
    /// </remarks>
    private IdeCompilation DesignTimeCompilation(string project, string configuration)
    {
        CommandResult restore = sdk.Dotnet("restore", project);
        Assert.True(restore.ExitCode == 0, restore.Tail());
        string[] designTime = ["msbuild", project, "-t:ResolveReferences;ResolveProjectReferences;Compile;CoreCompile",
            $"-p:Configuration={configuration}", "-p:DesignTimeBuild=true", "-p:SkipCompilerExecution=true",
            "-p:ProvideCommandLineArgs=true", "-p:BuildingProject=false"];

        CommandResult rider = sdk.Dotnet([.. designTime, "-p:JetBrainsDesignTimeBuild=true", "-getItem:Compile", "-getItem:Analyzer", "-getItem:AdditionalFiles"]);
        Assert.True(rider.ExitCode == 0, rider.Tail());
        using JsonDocument riderJson = JsonDocument.Parse(rider.StdOut);
        JsonElement riderItems = riderJson.RootElement.GetProperty("Items");

        CommandResult visualStudio = sdk.Dotnet([.. designTime, "-p:BuildingInsideVisualStudio=true",
            "-p:ShouldUnsetParentConfigurationAndPlatform=false", "-getItem:CscCommandLineArgs"]);
        Assert.True(visualStudio.ExitCode == 0, visualStudio.Tail());
        using JsonDocument visualStudioJson = JsonDocument.Parse(visualStudio.StdOut);

        return new IdeCompilation(
            FullPaths(riderItems, "Compile"),
            FullPaths(riderItems, "Analyzer"),
            FullPaths(riderItems, "AdditionalFiles"),
            [.. Identities(visualStudioJson.RootElement.GetProperty("Items"), "CscCommandLineArgs")]);

        /// <summary>
        /// The full paths of one item type's items: FullPath, not Identity, since the project's own
        /// sources are relative (Program.cs) and the written ones absolute.
        /// </summary>
        /// <param name="items">The Items object of MSBuild's -getItem JSON.</param>
        /// <param name="type">The item type.</param>
        /// <returns>The paths with forward slashes; none when the build left the type empty (MSBuild's JSON then omits it, rather than listing it empty).</returns>
        /// <exception cref="KeyNotFoundException">An item without FullPath metadata (MSBuild gives every file item one).</exception>
        static string[] FullPaths(JsonElement items, string type) => items.TryGetProperty(type, out JsonElement list)
            ? [.. list.EnumerateArray().Select(item => item.GetProperty("FullPath").GetString()!.Replace('\\', '/'))]
            : [];

        /// <summary>
        /// The identities of one item type's items, for items that are no files (the compiler's
        /// arguments, whose FullPath would be a path made of "/analyzer:...").
        /// </summary>
        /// <param name="items">The Items object of MSBuild's -getItem JSON.</param>
        /// <param name="type">The item type.</param>
        /// <returns>The identities with forward slashes; none when the build left the type empty.</returns>
        /// <exception cref="KeyNotFoundException">An item without Identity (MSBuild gives every item one).</exception>
        static IEnumerable<string> Identities(JsonElement items, string type) => items.TryGetProperty(type, out JsonElement list)
            ? list.EnumerateArray().Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/'))
            : [];
    }

    /// <summary>Deletes the bin/ and obj/ folders of the given configurations of test projects, which only these tests use.</summary>
    /// <param name="projects">The test projects' folder names.</param>
    /// <param name="configurations">The configurations.</param>
    private static void DeleteConfigurations(string[] projects, string[] configurations)
    {
        foreach (string project in projects)
        {
            foreach (string configuration in configurations)
            {
                foreach (string folder in new[] { Repo.TestProjectPath(project, "bin", configuration), Repo.TestProjectPath(project, "obj", configuration) })
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
            }
        }
    }
}
