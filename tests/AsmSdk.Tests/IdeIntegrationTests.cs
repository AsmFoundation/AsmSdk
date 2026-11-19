using System.Text.Json;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// What IDEs (Rider, Visual Studio, VS Code) see of an .asmproj: every file of the project
/// folder as an item, the asm item types declared so they are shown, the inputs and outputs of
/// their fast up-to-date checks, and design-time builds that never run the tools.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// These are checked through MSBuild itself (what an IDE asks MSBuild for), since no IDE runs
/// here. The design-time build replays the targets and properties Rider's MSBuild host uses.
/// </remarks>
public sealed class IdeIntegrationTests(SdkUnderTest sdk)
{
    /// <summary>A tool command line at normal verbosity (assemble, link or archive).</summary>
    private static readonly Regex ToolRun = new(@"(nasm(\.exe)?"" -f|lld(\.exe)?"" -flavor|llvm-ar(\.exe)?"" rcs)");

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
    /// AsmSource and AsmInclude are AvailableItemName items: Rider's MSBuild host and Visual
    /// Studio's project system show only the item types they know plus those, so without this an
    /// .asmproj looks empty in the IDE.
    /// </summary>
    [Fact]
    public void The_asm_item_types_are_declared_for_IDEs()
    {
        string[] available = Items("Exports", "libc", "AvailableItemName")["AvailableItemName"];
        Assert.Contains("AsmSource", available);
        Assert.Contains("AsmInclude", available);
    }

    /// <summary>
    /// Every file of the project folder is exactly one item, as in a .csproj: sources are
    /// AsmSource, includes AsmInclude, anything else (here the linker version script) None.
    /// The project file and bin/ and obj/ are no items, even after a build created them.
    /// </summary>
    [Fact]
    public void Every_file_of_the_project_folder_is_one_item()
    {
        CommandResult build = sdk.BuildTestProject("Exports");
        Assert.True(build.ExitCode == 0, build.Tail());
        Dictionary<string, string[]> items = Items("Exports", "libc", "AsmSource", "AsmInclude", "None");
        Assert.Equal(["exports.asm"], items["AsmSource"]);
        Assert.Equal(["exports.map"], items["None"]);
        Assert.Empty(items["AsmInclude"]);

        items = Items("LibcPackage", "libc", "AsmSource", "AsmInclude", "None");
        Assert.Equal(["libconly.asm"], items["AsmSource"]);
        Assert.Equal(["include/libconly.inc"], items["AsmInclude"]);
        Assert.Empty(items["None"]);
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
