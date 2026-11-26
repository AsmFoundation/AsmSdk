using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The dotnet new templates (src/AsmFoundation.Templates, packed by the fixture at the SDK's
/// version): the package's layout and version stamp, and what dotnet new writes from it, the
/// SDK version and every name form. The PlugAndPlay scenarios build, run and test what the
/// templates create; these tests check what the files say, without a restore.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// dotnet new runs with a .NET CLI home of its own (artifacts/test-run/templates/home), so the
/// templates are installed there and never into the developer's profile, and in a folder whose
/// empty global.json is where the asmnet template's global.json post action stops: without it,
/// that action would search up to the repository root and edit the AsmSdk repository's own
/// global.json.
/// </remarks>
public sealed class TemplateTests(SdkUnderTest sdk)
{
    /// <summary>The six templates' short names, as dotnet new lists them.</summary>
    private static readonly string[] ShortNames = ["asmexe", "asmlib", "asmnet", "asmstatic", "asmwasm", "asmwasmapp"];

    /// <summary>
    /// An MSBuild or NuGet warning line ("warning NU5128: ..."): dotnet pack prints no warning
    /// summary at its default verbosity.
    /// </summary>
    private static readonly Regex MsBuildWarning = new("warning [A-Z]+[0-9]+");

    /// <summary>The templates package in the local feed.</summary>
    private string Package => Path.Combine(Repo.Artifacts, "packages", $"AsmFoundation.Templates.{sdk.Version}.nupkg");

    /// <summary>The folder these tests instantiate templates in (with the CLI home and the global.json stop).</summary>
    private static string WorkFolder => Path.Combine(Repo.Artifacts, "test-run", "templates");

    /// <summary>The templates package packs without a warning.</summary>
    [Fact]
    public void The_templates_pack()
    {
        Assert.True(sdk.PackTemplates.ExitCode == 0 && !MsBuildWarning.IsMatch(sdk.PackTemplates.StdOut), sdk.PackTemplates.Tail());
        Assert.True(File.Exists(Package), $"no {Package}");
    }

    /// <summary>
    /// The package holds the six templates under content/, each with its .template.config
    /// (which NuGet would leave out, starting with a dot, unless told), and nothing compiled.
    /// </summary>
    [Fact]
    public void The_package_holds_the_six_templates()
    {
        Assert.Equal(
            new SortedSet<string>(StringComparer.Ordinal)
            {
                "AsmFoundation.Templates.nuspec", "LICENSE", "README.md", "icon.png",
                "content/asmexe/.template.config/dotnetcli.host.json", "content/asmexe/.template.config/template.json",
                "content/asmexe/AsmApp.1.asmproj", "content/asmexe/main.asm", "content/asmexe/main.s",
                "content/asmlib/.template.config/dotnetcli.host.json", "content/asmlib/.template.config/template.json",
                "content/asmlib/AsmLibrary.1.asmproj", "content/asmlib/add.asm", "content/asmlib/add.s",
                "content/asmnet/.template.config/dotnetcli.host.json", "content/asmnet/.template.config/template.json",
                "content/asmnet/AsmNet.1.App/AsmNet.1.App.csproj", "content/asmnet/AsmNet.1.App/Program.cs",
                "content/asmnet/AsmNet.1.Native/AsmNet.1.Native.asmproj", "content/asmnet/AsmNet.1.Native/add.asm",
                "content/asmnet/AsmNet.1.Native/add.s",
                "content/asmnet/AsmNet.1.Tests/AsmNet.1.Tests.csproj", "content/asmnet/AsmNet.1.Tests/NativeTests.cs",
                "content/asmnet/AsmNet.1.Tests/xunit.runner.json", "content/asmnet/AsmNet.1.slnx",
                "content/asmstatic/.template.config/dotnetcli.host.json", "content/asmstatic/.template.config/template.json",
                "content/asmstatic/AsmStatic.1.asmproj", "content/asmstatic/include/AsmStatic.1.inc",
                "content/asmstatic/mul.asm", "content/asmstatic/mul.s",
                "content/asmwasm/.template.config/dotnetcli.host.json", "content/asmwasm/.template.config/template.json",
                "content/asmwasm/AsmWasm.1.asmproj", "content/asmwasm/add.wasm.s", "content/asmwasm/run.mjs",
                "content/asmwasmapp/.template.config/dotnetcli.host.json", "content/asmwasmapp/.template.config/template.json",
                "content/asmwasmapp/AsmWasmApp.1.App/AsmWasmApp.1.App.csproj", "content/asmwasmapp/AsmWasmApp.1.App/Program.cs",
                "content/asmwasmapp/AsmWasmApp.1.App/main.mjs", "content/asmwasmapp/AsmWasmApp.1.App/runtimeconfig.template.json",
                "content/asmwasmapp/AsmWasmApp.1.Native/AsmWasmApp.1.Native.asmproj", "content/asmwasmapp/AsmWasmApp.1.Native/add.wasm.s",
                "content/asmwasmapp/AsmWasmApp.1.slnx",
            },
            SdkUnderTest.PackageEntries(Package));
    }

    /// <summary>
    /// Pack stamped the SDK version into every template: each SdkVersion parameter defaults to
    /// it (the value dotnet new writes into the projects' Sdk attribute), no stamp token is left
    /// anywhere, and each .asmproj names the parameter's token on its first line.
    /// </summary>
    [Fact]
    public void Every_template_defaults_to_the_sdk_version_it_was_packed_with()
    {
        using ZipArchive archive = ZipFile.OpenRead(Package);
        foreach (ZipArchiveEntry entry in archive.Entries.Where(e => e.FullName.StartsWith("content/", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("__ASMSDK_VERSION__", text);
            if (entry.Name == "template.json")
            {
                using JsonDocument json = JsonDocument.Parse(text);
                JsonElement version = json.RootElement.GetProperty("symbols").GetProperty("SdkVersion");
                Assert.Equal(sdk.Version, version.GetProperty("defaultValue").GetString());
                Assert.Equal("ASMSDK_VERSION", version.GetProperty("replaces").GetString());
            }
            else if (entry.Name.EndsWith(".asmproj", StringComparison.Ordinal))
            {
                Assert.StartsWith("<Project Sdk=\"AsmFoundation.Sdk/ASMSDK_VERSION\">", text);
            }
        }
    }

    /// <summary>
    /// dotnet new installs the package and creates each template: the projects name the SDK
    /// version inline (no global.json needed), and --sdk-version picks another.
    /// </summary>
    [Fact]
    public void Dotnet_new_writes_the_sdk_version_inline()
    {
        string output = Install();
        CommandResult library = DotnetNew(output, "asmlib", "-n", "Demo.Native", "-o", "lib");
        Assert.True(library.ExitCode == 0, library.Tail());
        Assert.StartsWith($"<Project Sdk=\"AsmFoundation.Sdk/{sdk.Version}\">", File.ReadAllText(Path.Combine(output, "lib", "Demo.Native.asmproj")));

        CommandResult pinned = DotnetNew(output, "asmexe", "-n", "Pinned", "-o", "pinned", "--sdk-version", "9.8.7");
        Assert.True(pinned.ExitCode == 0, pinned.Tail());
        Assert.StartsWith("<Project Sdk=\"AsmFoundation.Sdk/9.8.7\">", File.ReadAllText(Path.Combine(output, "pinned", "Pinned.asmproj")));
    }

    /// <summary>
    /// A name with a hyphen keeps it wherever a file, a path or a library name needs it
    /// (my-app.Native.asmproj, the .slnx, the ProjectReferences, the AsmBindings item's library,
    /// which must match the library's AssemblyName) and becomes my_app in C#: the test namespace,
    /// and the bindings' namespace, which the template names in the AsmBindings item, since the
    /// SDK's own would be MyApp.Native and the code says my_app.Native. Each occurrence in the
    /// template names its form (sourceName AsmNet.1: AsmNet.1 for the name as given, AsmNet._1
    /// for the namespace). With a sourceName whose forms were all the same text, the engine wrote
    /// my_demo into the paths of files it named my-demo (measured).
    /// </summary>
    [Fact]
    public void A_hyphenated_name_keeps_its_form_in_paths_and_becomes_a_namespace()
    {
        string output = Install();
        CommandResult created = DotnetNew(output, "asmnet", "-n", "my-app", "-o", "net");
        Assert.True(created.ExitCode == 0, created.Tail());
        string solution = Path.Combine(output, "net");
        Assert.Contains("Path=\"my-app.Native/my-app.Native.asmproj\" Type=\"13B669BE-BB05-4DDF-9536-439F39A36129\"",
            File.ReadAllText(Path.Combine(solution, "my-app.slnx")));
        foreach (string project in new[] { "my-app.App/my-app.App.csproj", "my-app.Tests/my-app.Tests.csproj" })
        {
            string text = File.ReadAllText(Path.Combine(solution, project));
            Assert.Contains("<ProjectReference Include=\"../my-app.Native/my-app.Native.asmproj\" />", text);
            Assert.Contains("<AsmBindings Include=\"my-app.Native\" Namespace=\"my_app.Native\" />", text);
        }

        Assert.Contains("my_app.Native.Add.AddInts(40, 2)", File.ReadAllText(Path.Combine(solution, "my-app.App", "Program.cs")));
        string tests = File.ReadAllText(Path.Combine(solution, "my-app.Tests", "NativeTests.cs"));
        Assert.Contains("namespace my_app.Tests;", tests);
        Assert.Contains("my_app.Native.Add.AddInts(a, b)", tests);
        Assert.DoesNotContain("AsmNet", tests);
    }

    /// <summary>
    /// The static library's include guard is made from the name, as an identifier: dots and
    /// hyphens become underscores, upper case (My.Shared-Lib -> MY_SHARED_LIB_INC), and the
    /// include file is named after the project.
    /// </summary>
    [Fact]
    public void The_static_library_include_is_named_and_guarded_after_the_project()
    {
        string output = Install();
        CommandResult created = DotnetNew(output, "asmstatic", "-n", "My.Shared-Lib", "-o", "static");
        Assert.True(created.ExitCode == 0, created.Tail());
        string include = File.ReadAllText(Path.Combine(output, "static", "include", "My.Shared-Lib.inc"));
        Assert.Contains("%ifndef MY_SHARED_LIB_INC", include);
        Assert.Contains("%define MY_SHARED_LIB_INC", include);
        Assert.Contains("%include \"My.Shared-Lib.inc\"", File.ReadAllText(Path.Combine(output, "static", "My.Shared-Lib.asmproj")));
    }

    /// <summary>
    /// The test runner choice, as in xUnit.net's own xunit3 template: the default (mtp-v2) takes
    /// xunit.v3.mtp-v2 alone and sets Microsoft.Testing.Platform in global.json (the empty one
    /// of the work folder, the nearest); vstest takes Microsoft.NET.Test.Sdk,
    /// xunit.runner.visualstudio and xunit.v3.mtp-off.
    /// </summary>
    [Fact]
    public void The_test_runner_choice_picks_xunits_packages()
    {
        string output = Install();
        CommandResult mtp = DotnetNew(output, "asmnet", "-n", "Mtp", "-o", "mtp");
        Assert.True(mtp.ExitCode == 0, mtp.Tail());
        string mtpProject = File.ReadAllText(Path.Combine(output, "mtp", "Mtp.Tests", "Mtp.Tests.csproj"));
        Assert.Contains("<PackageReference Include=\"xunit.v3.mtp-v2\"", mtpProject);
        Assert.DoesNotContain("Microsoft.NET.Test.Sdk", mtpProject);
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "global.json"))))
        {
            Assert.Equal("Microsoft.Testing.Platform", json.RootElement.GetProperty("test").GetProperty("runner").GetString());
        }

        CommandResult vstest = DotnetNew(output, "asmnet", "-n", "Classic", "-o", "vstest", "--test-runner", "vstest");
        Assert.True(vstest.ExitCode == 0, vstest.Tail());
        string vstestProject = File.ReadAllText(Path.Combine(output, "vstest", "Classic.Tests", "Classic.Tests.csproj"));
        Assert.Contains("<PackageReference Include=\"Microsoft.NET.Test.Sdk\"", vstestProject);
        Assert.Contains("<PackageReference Include=\"xunit.runner.visualstudio\"", vstestProject);
        Assert.Contains("<PackageReference Include=\"xunit.v3.mtp-off\"", vstestProject);
        Assert.DoesNotContain("xunit.v3.mtp-v2", vstestProject);
    }

    /// <summary>
    /// The code of each single-project template builds for all eight RIDs, not only for the
    /// machine a scenario runs on: x64 code through NASM for win-x64, linux-x64, linux-musl-x64
    /// and osx-x64, ARM64 code through llvm-mc for the other four, each output in its RID's
    /// format and name. The library exports add_ints on every OS, and the static library defines
    /// mul_ints in code for every RID: both sources name no section, which NASM's COFF output got
    /// wrong for the static library (see ImplicitUsingsTests). The projects restore the SDK under
    /// test from the local feed, through a nuget.config written next to them (the inline version
    /// names it).
    /// </summary>
    [Fact]
    public void Every_template_builds_for_all_eight_rids()
    {
        string output = Install();
        WriteLocalFeedConfig(output);
        string allRids = string.Join(';', CrossPlatformTests.PlatformCode.Keys);
        foreach ((string template, string name) in new[] { ("asmlib", "Lib8"), ("asmexe", "Exe8"), ("asmstatic", "Static8") })
        {
            CommandResult created = DotnetNew(output, template, "-n", name);
            Assert.True(created.ExitCode == 0, created.Tail());
            CommandResult build = Dotnet(output, "build", Path.Combine(name, name + ".asmproj"), "-c", "Release",
                $"-p:RuntimeIdentifiers=\"{allRids}\"", "-nologo", "-nodeReuse:false");
            Assert.True(build.ExitCode == 0, $"{template}: {build.Tail()}");
            foreach (string rid in CrossPlatformTests.PlatformCode.Keys)
            {
                bool windows = rid.StartsWith("win-", StringComparison.Ordinal);
                string file = template switch
                {
                    "asmlib" => Path.Combine("freestanding", rid, CrossPlatformTests.LibraryFile(rid, name)),
                    "asmexe" => Path.Combine("libc", rid, windows ? name + ".exe" : name),
                    _ => Path.Combine("freestanding", rid, windows ? name + ".lib" : $"lib{name}.a"),
                };
                string path = Path.Combine(output, name, "bin", "Release", file);
                Assert.True(File.Exists(path), $"{template}, {rid}: no {path}");
                if (template != "asmstatic")
                {
                    Assert.Contains($"Format: {CrossPlatformTests.Format[rid]}", sdk.ReadObj(path, "--file-headers"));
                }
                else
                {
                    // The library is archived, not linked, so its build succeeds with undefined
                    // symbols: mul_ints must be defined, in code, for every RID. mul.asm names no
                    // section, and on win-x64 NASM's COFF output wrote mul_ints as undefined
                    // until ImplicitUsings named .text on every OS (2026-11-23).
                    ImplicitUsingsTests.AssertDefinedInText(sdk.ReadObj(path, "--symbols"), "mul_ints", $"{template}, {rid}");
                }

                if (template == "asmlib")
                {
                    // add.asm names no section either, and the library must export add_ints on
                    // every OS: the format check alone would pass a library that exports nothing.
                    AssertExports(path, rid, "add_ints");
                }
            }
        }
    }

    /// <summary>
    /// asmwasm builds its library for both WebAssembly runtimes on any build machine, nothing
    /// installed: a module per RID that exports add_ints, and an archive beside it. Its run.mjs
    /// then calls the module from Node, as the template's README says: "dotnet build", then
    /// "node run.mjs" prints add_ints(40, 2) = 42 (where Node exists).
    /// </summary>
    [Fact]
    public void The_webassembly_library_template_builds_and_runs_in_node()
    {
        string output = Install();
        WriteLocalFeedConfig(output);
        CommandResult created = DotnetNew(output, "asmwasm", "-n", "Calc");
        Assert.True(created.ExitCode == 0, created.Tail());
        CommandResult build = Dotnet(output, "build", Path.Combine("Calc", "Calc.asmproj"), "-nologo", "-nodeReuse:false");
        Assert.True(build.ExitCode == 0, build.Tail());
        foreach (string rid in new[] { "browser-wasm", "wasi-wasm" })
        {
            string module = Path.Combine(output, "Calc", "bin", "Debug", "freestanding", rid, "Calc.wasm");
            Assert.True(File.Exists(module), $"{rid}: no {module}");
            Assert.Contains("add_ints", WasmBinary.Read(module).ExportedFunctions);
            Assert.True(File.Exists(Path.Combine(output, "Calc", "bin", "Debug", "freestanding", rid, "Calc.a")), $"{rid}: no archive");
        }

        if (!Command.OnPath("node"))
        {
            Assert.Skip("No Node on PATH to run run.mjs here (the library built and was checked above)");
        }

        CommandResult run = Command.Run("node", [Path.Combine("Calc", "run.mjs")], output);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("add_ints(40, 2) = 42", run.StdOut.Trim());
    }

    /// <summary>
    /// asmwasmapp makes a solution whose .NET browser-wasm app links the WebAssembly library and
    /// calls it, and "dotnet run" runs it in Node, as the README says. The name has dots
    /// (Demo.Wasm): the P/Invoke module, "Demo.Wasm.Native", reaches the app's P/Invoke table with
    /// them. Needs .NET's native WebAssembly build for net10.0 (the wasm-tools workload, CI's
    /// Linux x64 and Windows x64 runners), as the other .NET WebAssembly app tests do.
    /// </summary>
    [Fact]
    public void The_webassembly_app_template_links_the_library_and_runs()
    {
        WebAssemblyTests.RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        string output = Install();
        WriteLocalFeedConfig(output);
        CommandResult created = DotnetNew(output, "asmwasmapp", "-n", "Demo.Wasm");
        Assert.True(created.ExitCode == 0, created.Tail());
        string app = Path.Combine(output, "Demo.Wasm", "Demo.Wasm.App", "Demo.Wasm.App.csproj");
        WebAssemblyTests.RequireWasmNativeBuild(sdk, app, "UsingBrowserRuntimeWorkload");
        CommandResult build = Dotnet(output, "build", Path.Combine("Demo.Wasm", "Demo.Wasm.slnx"), "-nologo", "-nodeReuse:false");
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Dotnet(output, "run", "--project", Path.Combine("Demo.Wasm", "Demo.Wasm.App"), "--no-build");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("add_ints(40, 2) = 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// Writes the nuget.config a work folder's projects restore through: the SDK under test from
    /// the local feed (the inline version names it), everything else from nuget.org.
    /// </summary>
    /// <param name="folder">The work folder.</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    private static void WriteLocalFeedConfig(string folder) =>
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{Path.Combine(Repo.Artifacts, "packages")}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local"><package pattern="AsmFoundation.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);

    /// <summary>
    /// Makes a fresh work folder (an empty global.json in it, see the remarks) and installs the
    /// templates package from its file into the folder's own CLI home, once per test.
    /// </summary>
    /// <returns>The folder the templates are instantiated in.</returns>
    /// <exception cref="Xunit.Sdk.XunitException">dotnet new install failed or did not list the four templates.</exception>
    private string Install()
    {
        Assert.True(File.Exists(Package), $"no {Package}: {sdk.PackTemplates.Tail()}");
        // A folder per test: the tests may not share instantiated files or the global.json the
        // asmnet template edits.
        string folder = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "global.json"), "{}\n");
        CommandResult install = Dotnet(folder, "new", "install", Package);
        Assert.True(install.ExitCode == 0, install.Tail());
        foreach (string shortName in ShortNames)
        {
            Assert.Contains(shortName, install.StdOut);
        }

        return folder;
    }

    /// <summary>Runs dotnet new with a template, without its restore (no package is needed to check the files).</summary>
    /// <param name="folder">The work folder (the working directory).</param>
    /// <param name="arguments">The template's short name, then its options.</param>
    /// <returns>The command's result.</returns>
    private CommandResult DotnetNew(string folder, params string[] arguments) =>
        Dotnet(folder, ["new", .. arguments, "--no-restore"]);

    /// <summary>
    /// Runs dotnet in a work folder with the test environment and the folder's own CLI home
    /// (DOTNET_CLI_HOME), without the first-run steps a fresh home would take outside it.
    /// </summary>
    /// <param name="folder">The work folder: the working directory, and the home's parent.</param>
    /// <param name="arguments">The arguments after dotnet.</param>
    /// <returns>The command's result.</returns>
    private CommandResult Dotnet(string folder, params string[] arguments)
    {
        var environment = new Dictionary<string, string?>(sdk.TestEnvironment, StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(folder, ".cli-home"),
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        };
        return Command.Run("dotnet", arguments, folder, environment);
    }

    /// <summary>
    /// Asserts that a shared library exports a function, as each OS records it: in the export
    /// table of a DLL, as an external symbol with Mach-O's underscore in a .dylib, in the
    /// dynamic symbol table of an ELF .so.
    /// </summary>
    /// <param name="library">The library file.</param>
    /// <param name="rid">Its runtime identifier, which picks the OS's record.</param>
    /// <param name="name">The function's C name.</param>
    /// <exception cref="Xunit.Sdk.XunitException">The library does not export the function.</exception>
    private void AssertExports(string library, string rid, string name)
    {
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            Assert.Contains($"Name: {name}", sdk.ReadObj(library, "--coff-exports"));
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            Assert.Matches($@"Name: _{name} \(\d+\)\s+Extern", sdk.ReadObj(library, "--symbols"));
        }
        else
        {
            Assert.Matches($@"Name: {name} \(\d+\)", sdk.ReadObj(library, "--dyn-syms"));
        }
    }
}
