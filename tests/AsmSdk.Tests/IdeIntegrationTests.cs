using System.Text.Json;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// What IDEs (Rider, Visual Studio, VS Code) see of an .asmproj: every file of the project
/// folder as an item (the sources as Compile items, everything else as None, as in a .csproj),
/// the inputs and outputs of their fast up-to-date checks, and design-time builds that never
/// run the tools.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// These are checked through MSBuild itself (what an IDE asks MSBuild for), since no IDE runs
/// here. The design-time build replays the targets and properties Rider's MSBuild host uses.
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
}
