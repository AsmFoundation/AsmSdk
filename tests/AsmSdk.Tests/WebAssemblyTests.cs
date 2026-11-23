using System.Text;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// WebAssembly, AsmSdk's third target (the owner's choice of 2026-11-23: LLVM's WebAssembly
/// assembly in .s files, for browser-wasm and wasi-wasm, with three outputs: a .NET WebAssembly
/// app, standalone modules, WASI). Every machine builds and inspects every output (the bundled
/// llvm-mc and wasm-ld run everywhere, and <see cref="WasmBinary"/> reads them); what runs needs
/// its host, and a test without one skips with the reason:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Node runs the modules (exports called from JavaScript) and the browser-wasm .NET apps;</item>
/// <item>wasmtime runs the WASI program, dotnet run's way and directly, and the WASI .NET app;</item>
/// <item>the .NET apps need the dotnet on PATH to have the wasm-tools workload (browser-wasm) or
/// the wasi-experimental workload with the WASI SDK 25.0 in WASI_SDK_PATH (wasi-wasm): CI installs
/// them on the Linux x64 runner.</item>
/// </list>
/// <para>
/// The projects (tests/projects): WasmLibrary (a library: module and archive, both RIDs),
/// WasmStatic (a static library), WasmObjects (object files), WasmProgram (a WASI command that
/// links WasmLibrary's archive), WasmExports (an exports file), WasmBrowserApp, WasiApp and
/// WasmBrowserPackageApp (.NET apps that link them), and the rule projects WasmNasm, WasmNoSource,
/// WasmConstant, WasmLibc and WasmRules, which must fail with their diagnostics.
/// </para>
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class WebAssemblyTests(SdkUnderTest sdk)
{
    /// <summary>The two WebAssembly RIDs.</summary>
    public static TheoryData<string> WasmRids => new() { "browser-wasm", "wasi-wasm" };

    /// <summary>What wl_platform() answers in a Release build, by RID: 1 BROWSER or 2 WASI, + 10 RELEASE + 100 FREESTANDING.</summary>
    private static readonly Dictionary<string, int> PlatformAnswer = new() { ["browser-wasm"] = 111, ["wasi-wasm"] = 112 };

    /// <summary>A file of a test project's Release build output for a RID (freestanding).</summary>
    /// <param name="project">The test project's name.</param>
    /// <param name="rid">The RID.</param>
    /// <param name="file">The file's name.</param>
    /// <returns>The full path.</returns>
    private static string Output(string project, string rid, string file) =>
        Repo.TestProjectPath(project, "bin", "Release", "freestanding", rid, file);

    /// <summary>Builds a test project in Release once, asserting it succeeded.</summary>
    /// <param name="project">The test project's name.</param>
    private void Build(string project)
    {
        CommandResult build = sdk.BuildTestProject(project);
        Assert.True(build.ExitCode == 0, $"{project}: {build.Tail()}");
    }

    /// <summary>Skips the calling test when Node is not on PATH.</summary>
    /// <exception cref="Exception">Xunit's skip exception.</exception>
    private static void RequireNode()
    {
        if (!Command.OnPath("node"))
        {
            Assert.Skip("No Node on PATH to run WebAssembly here");
        }
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
    /// How the .NET WebAssembly app tests treat a machine that cannot run them, from
    /// <c>ASMSDK_TEST_WASM_APPS</c>: auto (the default: run, or skip with the reason), required
    /// (fail instead of skipping: CI's Linux x64 runner installs the workloads, and there a skip
    /// would hide that the apps were never linked), none (skip).
    /// </summary>
    /// <exception cref="InvalidOperationException">ASMSDK_TEST_WASM_APPS holds an unknown value (thrown by the initializer).</exception>
    private static string WasmAppsMode { get; } = Environment.GetEnvironmentVariable("ASMSDK_TEST_WASM_APPS") switch
    {
        null or "auto" => "auto",
        "required" => "required",
        "none" => "none",
        string other => throw new InvalidOperationException($"ASMSDK_TEST_WASM_APPS={other}: expected auto, required or none"),
    };

    /// <summary>
    /// Lets a .NET WebAssembly app test go on when a prerequisite holds; otherwise skips it with
    /// the reason, or fails it when the run is required (<see cref="WasmAppsMode"/>).
    /// </summary>
    /// <param name="holds">Whether the prerequisite is met.</param>
    /// <param name="reason">What is missing, for the skip or failure message.</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the run is required.</exception>
    private static void RequireForWasmApps(bool holds, string reason)
    {
        if (WasmAppsMode == "none")
        {
            Assert.Skip("ASMSDK_TEST_WASM_APPS=none");
        }

        if (holds)
        {
            return;
        }

        if (WasmAppsMode == "required")
        {
            Assert.Fail(reason + " (ASMSDK_TEST_WASM_APPS=required)");
        }

        Assert.Skip(reason);
    }

    /// <summary>
    /// Requires .NET's native WebAssembly build for a .NET app of tests/projects, as .NET itself
    /// decides it for the app's target framework and RID (see <see cref="RequireForWasmApps"/>).
    /// </summary>
    /// <param name="app">The app's project name (tests/projects/&lt;app&gt;/&lt;app&gt;.csproj).</param>
    /// <param name="property">UsingBrowserRuntimeWorkload (browser-wasm) or UsingWasiRuntimeWorkload (wasi-wasm).</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the run is required.</exception>
    /// <remarks>
    /// The question is .NET's, not a workload list's. The workload a net10.0 app needs is
    /// wasm-tools on SDK 10 but wasm-tools-net10 on SDK 11, and GitHub's windows-2025 image has
    /// SDK 11.0.100 with Visual Studio's wasm-tools, which serves net11.0 only: a name check
    /// passed there, and the app built without its AppBundle (CI run 45275929352). The property
    /// is the switch .NET's own imports test, and the one the SDK's ASM0054 check reads, so these
    /// tests run exactly where the apps can be linked. Evaluation only, once per app.
    /// </remarks>
    private void RequireWasmNativeBuild(string app, string property)
    {
        if (WasmAppsMode == "none")
        {
            Assert.Skip("ASMSDK_TEST_WASM_APPS=none");
        }

        string project = Repo.TestProjectPath(app, app + ".csproj");
        CommandResult answer = sdk.Once($"{app} {property}", () => sdk.DotnetCommand("msbuild", project, $"-getProperty:{property}", "-nologo"));
        CommandResult version = sdk.Once("dotnet --version", () => sdk.DotnetCommand("--version"));
        RequireForWasmApps(answer.ExitCode == 0 && answer.StdOut.Trim() == "true",
            $"The dotnet on PATH ({version.StdOut.Trim()}) has no native WebAssembly build for {app}: {property} is '{answer.StdOut.Trim()}' " +
            $"(exit {answer.ExitCode}). It needs the wasm-tools workload for browser-wasm, or wasi-experimental and WASI_SDK_PATH for wasi-wasm, " +
            "of the app's .NET version (net10.0: wasm-tools-net10 on a later SDK)");
    }

    /// <summary>
    /// Every library build writes, per wasm RID, a module (wasmlib.wasm) that exports exactly the
    /// FUNCTIONs and its memory (export-dynamic: not the HIDDEN_FUNCTION nor the LOCAL_FUNCTION)
    /// and imports nothing, and an archive (wasmlib.a, with a symbol index for wasm-ld) for the
    /// .NET apps and asm programs that link it. No lib prefix on either (see Properties.targets).
    /// The module holds ws_mul, from the WasmStatic static library it links, and exports it too
    /// (a visible global of the module); the archive holds WasmStatic's member as well (llvm-ar's
    /// ADDLIB), since the reference is private and the archive's users get no wasmstatic.a.
    /// </summary>
    /// <param name="rid">A wasm RID.</param>
    [Theory]
    [MemberData(nameof(WasmRids))]
    public void A_library_is_a_module_and_an_archive(string rid)
    {
        Build("WasmLibrary");
        WasmBinary module = WasmBinary.Read(Output("WasmLibrary", rid, "wasmlib.wasm"));
        Assert.Empty(module.Imports);
        Assert.Equal(["wl_add", "wl_twice", "wl_platform", "wl_sum", "wl_table_sum", "wl_simd", "wl_square", "ws_mul"], module.ExportedFunctions.ToArray());
        Assert.Contains(new WasmEntry("", "memory", "memory"), module.Exports);

        byte[] archive = File.ReadAllBytes(Output("WasmLibrary", rid, "wasmlib.a"));
        string text = Encoding.ASCII.GetString(archive);
        Assert.Equal("!<arch>\n", text[..8]);
        // The GNU symbol table comes first, with every global the objects define, hidden ones
        // included (wasm-ld resolves wl_twice's call through it), then the members by name: the
        // library's own object and the static library's.
        Assert.Contains("wl_add", text);
        Assert.Contains("wl_hidden", text);
        Assert.Contains("ws_mul", text);
        Assert.Contains("wasmlib.wasm.o/", text);
        Assert.Contains("mul.wasm.o/", text);
    }

    /// <summary>
    /// The modules compute under Node: each export, the SDK's defines as wl_platform sees them
    /// (111 browser, 112 WASI), a loop over linear memory, a table in read-only data, SIMD128,
    /// and the static library's ws_mul, directly and through wl_square.
    /// </summary>
    /// <param name="rid">A wasm RID.</param>
    [Theory]
    [MemberData(nameof(WasmRids))]
    public void A_library_module_runs_in_node(string rid)
    {
        RequireNode();
        Build("WasmLibrary");
        // The sum's input goes to a page the module grows for it: nothing of the module's own
        // data lives there.
        const string Script =
            "const m = new WebAssembly.Module(require('fs').readFileSync(process.argv[1]));" +
            "const e = new WebAssembly.Instance(m, {}).exports;" +
            "const base = e.memory.grow(1) * 65536;" +
            "new Int32Array(e.memory.buffer, base, 3).set([10, 20, 12]);" +
            "console.log([e.wl_add(40, 2), e.wl_twice(21), e.wl_platform(), e.wl_table_sum(), e.wl_simd(21), e.wl_sum(base, 3), e.ws_mul(6, 7), e.wl_square(6)].join(' '));";
        CommandResult run = Command.Run("node", ["-e", Script, Output("WasmLibrary", rid, "wasmlib.wasm")]);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal($"42 42 {PlatformAnswer[rid]} 42 42 42 42 36", run.StdOut.Trim());
    }

    /// <summary>
    /// A WebAssembly source is assembled through the wrapper the SDK writes: the defines as labels
    /// (llvm-mc's wasm objects hold no defsym constant), the platform layer (ImplicitUsings:
    /// asmsdk-wasm.inc itself), then the source behind a line marker. The defines are the ones a
    /// native build gets as defsym.
    /// </summary>
    [Fact]
    public void The_defines_reach_a_wasm_source_as_labels()
    {
        Build("WasmLibrary");
        string wrapper = Repo.TestProjectPath("WasmLibrary", "obj", "Release", "freestanding", "browser-wasm", "asm", "implicit", "src", "wasmlib.wasm.s");
        string[] lines = File.ReadAllLines(wrapper);
        Assert.Contains(".section .data.asmsdk_defines,\"\",@", lines);
        Assert.Contains("BROWSER:", lines);
        Assert.Contains("WASM:", lines);
        Assert.Contains("RELEASE:", lines);
        Assert.Contains("FREESTANDING:", lines);
        Assert.Contains(".size WASM, 0", lines);
        Assert.DoesNotContain("WASI:", lines);
        // Back to code with .section: a bare .text does nothing in llvm-mc's wasm parser.
        Assert.Contains(".section .text,\"\",@", lines);
        // The wasm layer itself, not asmsdk-gnu-implicit.inc: its dispatch would need WASM, which
        // DisableImplicitFrameworkDefines=true takes away.
        Assert.Contains(".include \"asmsdk-wasm.inc\"", lines);
        Assert.Contains(lines, line => line.StartsWith("# 1 \"", StringComparison.Ordinal) && line.EndsWith("/WasmLibrary/src/wasmlib.wasm.s\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// A Debug build's DWARF names the source itself, not the generated wrapper that llvm-mc
    /// assembled: the line marker puts the source's folder (src/ of the project, as the marker
    /// spells it, NUL-terminated in DWARF's string table) in the line table's directories, which
    /// an .include of the source never did (llvm-mc names only its main file there, the wrapper
    /// in obj/.../implicit/src/; measured with llvm-dwarfdump). The project folder alone would
    /// prove nothing: it is llvm-mc's working directory, DWARF's directory 0 in any case.
    /// </summary>
    [Fact]
    public void A_debug_object_names_the_source()
    {
        CommandResult build = sdk.Once("build WasmLibrary Debug", () => sdk.Dotnet("build", Repo.TestProject("WasmLibrary"), "-c", "Debug"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string objectFile = Repo.TestProjectPath("WasmLibrary", "obj", "Debug", "freestanding", "wasi-wasm", "asm", "obj", "src", "wasmlib.wasm.o");
        WasmBinary debug = WasmBinary.Read(objectFile);
        Assert.Contains(".debug_line", debug.CustomSections);
        string sourceFolder = Repo.TestProjectPath("WasmLibrary", "src").Replace('\\', '/');
        Assert.Contains(sourceFolder + "\0", Encoding.UTF8.GetString(File.ReadAllBytes(objectFile)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A static library is an archive alone; object files are objects alone, wasm objects that
    /// import the linear memory, as every relocatable wasm object does.
    /// </summary>
    /// <param name="rid">A wasm RID.</param>
    [Theory]
    [MemberData(nameof(WasmRids))]
    public void Static_libraries_and_objects_build_for_wasm(string rid)
    {
        Build("WasmStatic");
        Assert.True(File.Exists(Output("WasmStatic", rid, "wasmstatic.a")), "no wasmstatic.a");
        Assert.False(File.Exists(Output("WasmStatic", rid, "wasmstatic.wasm")), "a static library has no module");

        Build("WasmObjects");
        WasmBinary objectFile = WasmBinary.Read(Repo.TestProjectPath("WasmObjects", "obj", "Release", "freestanding", rid, "asm", "obj", "sub.wasm.o"));
        Assert.Contains(objectFile.Imports, import => import.Name == "__linear_memory" && import.Kind == "memory");
        Assert.Contains("linking", objectFile.CustomSections);
    }

    /// <summary>An exports file decides a module's exports: we_exported, not we_internal, and the memory.</summary>
    [Fact]
    public void An_exports_file_decides_a_modules_exports()
    {
        Build("WasmExports");
        WasmBinary module = WasmBinary.Read(Output("WasmExports", "wasi-wasm", "wasmexports.wasm"));
        Assert.Equal(["we_exported"], module.ExportedFunctions.ToArray());
        Assert.Contains(new WasmEntry("", "memory", "memory"), module.Exports);
    }

    /// <summary>
    /// A WASI program is a WASI command: it imports fd_write and proc_exit from
    /// wasi_snapshot_preview1, exports _start and its memory, and contains wl_add from the
    /// WasmLibrary archive its ProjectReference links (a module is never a link input).
    /// </summary>
    [Fact]
    public void A_wasi_program_is_a_command_that_links_the_library_archive()
    {
        Build("WasmProgram");
        WasmBinary program = WasmBinary.Read(Output("WasmProgram", "wasi-wasm", "wasmhello.wasm"));
        Assert.Equal(
            [new WasmEntry("wasi_snapshot_preview1", "fd_write", "function"), new WasmEntry("wasi_snapshot_preview1", "proc_exit", "function")],
            program.Imports.ToArray());
        Assert.Equal(["_start"], program.ExportedFunctions.ToArray());
        Assert.Contains(new WasmEntry("", "memory", "memory"), program.Exports);
    }

    /// <summary>The WASI program runs in wasmtime: its line on standard output, and wl_add(40, 2) as its exit code.</summary>
    [Fact]
    public void A_wasi_program_runs_in_wasmtime()
    {
        RequireWasmtime();
        Build("WasmProgram");
        CommandResult run = Command.Run("wasmtime", ["run", Output("WasmProgram", "wasi-wasm", "wasmhello.wasm")]);
        Assert.True(run.ExitCode == 42, $"exit {run.ExitCode}, want 42: {run.Output}");
        Assert.Equal("hello from WebAssembly", run.StdOut.Trim());
    }

    /// <summary>dotnet run starts a wasi-wasm program in wasmtime (AsmSelectRunTarget's WASI step) and returns its exit code.</summary>
    [Fact]
    public void Dotnet_run_starts_a_wasi_program_in_wasmtime()
    {
        RequireWasmtime();
        CommandResult run = sdk.DotnetRun(Repo.TestProject("WasmProgram"), ["-c", "Release"]);
        Assert.True(run.ExitCode == 42, $"exit {run.ExitCode}, want 42: {run.Tail()}");
        Assert.Contains("hello from WebAssembly", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// What cannot work stops the build with the fix: a NASM file for WebAssembly (ASM0021), a wasm
    /// RID no source builds (ASM0023, with the wasm qualifiers), an assembler constant the wasm
    /// writer refuses (ASM0019, instead of llvm-mc's crash alone), a libc module (ASM0016).
    /// </summary>
    /// <param name="project">The rule project.</param>
    /// <param name="code">The diagnostic it must stop with.</param>
    /// <param name="text">A part of the diagnostic's text that names the fix or the cause.</param>
    [Theory]
    [InlineData("WasmNasm", "ASM0021", "Write WebAssembly in LLVM's WebAssembly assembly")]
    [InlineData("WasmNoSource", "ASM0023", "name.wasm.s")]
    [InlineData("WasmConstant", "ASM0019", "LIMIT: absolute addressing not supported!")]
    [InlineData("WasmLibc", "ASM0016", "links no C library")]
    public void A_wasm_rule_stops_the_build(string project, string code, string text)
    {
        CommandResult build = sdk.BuildTestProject(project);
        Assert.True(build.ExitCode != 0, $"{project} built, but must fail with {code}: {build.Tail()}");
        Assert.Contains($"error {code}", build.StdOut, StringComparison.Ordinal);
        Assert.Contains(text, build.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rules for settings: a program for the browser (ASM0017), a define with a value
    /// (ASM0018, labels have none), a pattern in a module's exports file (ASM0030, wasm-ld takes
    /// exact names). WasmRules builds as it is; each setting alone stops it.
    /// </summary>
    /// <param name="setting">The dotnet build arguments that break it.</param>
    /// <param name="code">The diagnostic.</param>
    [Theory]
    [InlineData("-p:OutputType=Exe -r browser-wasm", "ASM0017")]
    [InlineData("-p:DefineConstants=LEVEL=3", "ASM0018")]
    [InlineData("-p:ExportsFile=wild.map", "ASM0030")]
    public void A_wasm_setting_rule_stops_the_build(string setting, string code)
    {
        Build("WasmRules");
        CommandResult build = sdk.BuildTestProject("WasmRules", setting.Split(' '));
        Assert.True(build.ExitCode != 0, $"WasmRules {setting} built, but must fail with {code}: {build.Tail()}");
        Assert.Contains($"error {code}", build.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without the SDK's platform defines (DisableImplicitFrameworkDefines=true takes WASM away),
    /// a wasm source still gets its layer: the wrapper includes asmsdk-wasm.inc itself, where a
    /// detour through asmsdk-gnu.inc's ".ifdef WASM" would take the native branch and stop on its
    /// .set guard (ASM0019).
    /// </summary>
    [Fact]
    public void A_wasm_build_without_the_platform_defines_keeps_its_layer()
    {
        CommandResult build = sdk.BuildTestProject("WasmRules", "-p:DisableImplicitFrameworkDefines=true");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>
    /// An IDE's fast up-to-date check knows each wasm RID's output, as it knows the native ones:
    /// the RID outer build lists wasmlib.wasm (a library's module, no lib prefix) and
    /// wasmstatic.a per RID, the files the inner builds write.
    /// </summary>
    [Fact]
    public void The_ide_up_to_date_check_knows_each_wasm_output()
    {
        Assert.Equal(
            ["bin/Debug/freestanding/browser-wasm/wasmlib.wasm", "bin/Debug/freestanding/wasi-wasm/wasmlib.wasm"],
            UpToDateCheckBuilt("WasmLibrary"));
        Assert.Equal(
            ["bin/Debug/freestanding/browser-wasm/wasmstatic.a", "bin/Debug/freestanding/wasi-wasm/wasmstatic.a"],
            UpToDateCheckBuilt("WasmStatic"));
    }

    /// <summary>
    /// The UpToDateCheckBuilt items of a test project's RID outer build (Debug, as an IDE loads
    /// it), each as the end of its path from the project folder, in order.
    /// </summary>
    /// <param name="project">The test project's name.</param>
    /// <returns>The items' paths relative to the project folder, forward slashes, sorted.</returns>
    private string[] UpToDateCheckBuilt(string project)
    {
        // Restore first: evaluation needs the SDK resolved from the isolated NuGet cache.
        CommandResult restore = sdk.Once($"restore {project}", () => sdk.Dotnet("restore", Repo.TestProject(project)));
        Assert.True(restore.ExitCode == 0, restore.Tail());
        CommandResult result = sdk.Dotnet("msbuild", Repo.TestProject(project), "-getItem:UpToDateCheckBuilt");
        Assert.True(result.ExitCode == 0, result.Tail());
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(result.StdOut);
        string folder = Repo.TestProjectPath(project).Replace('\\', '/').TrimEnd('/') + "/";
        return json.RootElement.GetProperty("Items").GetProperty("UpToDateCheckBuilt").EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString()!.Replace('\\', '/'))
            .Select(path => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? path[folder.Length..] : path)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A libc static library builds for wasm: its archive is linked later, by an app whose build brings the C library.</summary>
    [Fact]
    public void A_libc_static_library_builds_for_wasm()
    {
        CommandResult build = sdk.BuildTestProject("WasmLibc", "-p:OutputType=StaticLibrary");
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.True(File.Exists(Repo.TestProjectPath("WasmLibc", "bin", "Release", "libc", "wasi-wasm", "wasmlibc.a")), "no wasmlibc.a");
    }

    /// <summary>
    /// A wasm library's package carries its archive and its module under native/&lt;rid&gt;/&lt;tfm&gt;/
    /// and nothing under runtimes/, where NuGet would copy the module into every .NET app's
    /// output; its props give a .NET WebAssembly app the archive as a NativeFileReference.
    /// </summary>
    [Fact]
    public void A_wasm_package_carries_archives_and_modules_under_native()
    {
        CommandResult pack = PackWasmLibrary();
        Assert.True(pack.ExitCode == 0, pack.Tail());
        SortedSet<string> entries = SdkUnderTest.PackageEntries(Path.Combine(Repo.Artifacts, "packages", "Test.WasmLibrary.1.0.0.nupkg"));
        foreach (string rid in new[] { "browser-wasm", "wasi-wasm" })
        {
            Assert.Contains($"native/{rid}/freestanding/wasmlib.a", entries);
            Assert.Contains($"native/{rid}/freestanding/wasmlib.wasm", entries);
        }

        Assert.DoesNotContain(entries, entry => entry.StartsWith("runtimes/", StringComparison.Ordinal));
    }

    /// <summary>Packs WasmLibrary into the local feed as Test.WasmLibrary 1.0.0, once.</summary>
    /// <returns>The pack's result.</returns>
    private CommandResult PackWasmLibrary() =>
        sdk.Once("pack WasmLibrary", () => sdk.Dotnet("pack", Repo.TestProject("WasmLibrary"), "-c", "Release"));

    /// <summary>
    /// A .NET browser-wasm app links a library, a static library and object files from asm
    /// projects into its runtime and calls each through P/Invoke, under Node: the consumer
    /// targets hand them over as NativeFileReference, with the projects' AssemblyNames as the
    /// P/Invoke modules (an object file's own name would be "sub.wasm").
    /// </summary>
    [Fact]
    public void A_browser_wasm_dotnet_app_links_asm_projects()
    {
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here");
        RequireWasmNativeBuild("WasmBrowserApp", "UsingBrowserRuntimeWorkload");
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmBrowserApp", "WasmBrowserApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string bundle = Repo.TestProjectPath("WasmBrowserApp", "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
        // Said here rather than by the process start ("The directory name is invalid"): a build
        // without .NET's native WebAssembly build succeeds without a bundle.
        Assert.True(Directory.Exists(bundle), $"No AppBundle after the build: {build.Tail()}");
        CommandResult run = Command.Run("node", ["main.mjs"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("browser-wasm 42 42 111 42 42 42 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// The same from the library's package, through the package's own props (no AsmFoundation.Sdk
    /// in the app), including ws_mul, which only the package's archive carries: the static
    /// library is a private reference of the library, so the package depends on nothing (a
    /// dependency on WasmStatic, which has no package, would stop the restore with NU1101).
    /// </summary>
    [Fact]
    public void A_browser_wasm_dotnet_app_links_an_asm_package()
    {
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here");
        RequireWasmNativeBuild("WasmBrowserPackageApp", "UsingBrowserRuntimeWorkload");
        CommandResult pack = PackWasmLibrary();
        Assert.True(pack.ExitCode == 0, pack.Tail());
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmBrowserPackageApp", "WasmBrowserPackageApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string bundle = Repo.TestProjectPath("WasmBrowserPackageApp", "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
        Assert.True(Directory.Exists(bundle), $"No AppBundle after the build: {build.Tail()}");
        CommandResult run = Command.Run("node", ["main.mjs"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("browser-wasm 42 42 111 42 42 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// A .NET wasi-wasm app links the same three into its WASI runtime and runs in wasmtime (with
    /// WASI HTTP, which .NET's WASI runtime imports): the consumer targets also name the P/Invoke
    /// modules for .NET 10's WASI build, which would otherwise leave them out of its table.
    /// </summary>
    [Fact]
    public void A_wasi_dotnet_app_links_asm_projects()
    {
        RequireForWasmApps(Command.OnPath("wasmtime"), "No wasmtime on PATH to run the WASI app here (https://wasmtime.dev)");
        RequireWasmNativeBuild("WasiApp", "UsingWasiRuntimeWorkload");
        string? wasiSdk = Environment.GetEnvironmentVariable("WASI_SDK_PATH");
        RequireForWasmApps(!string.IsNullOrEmpty(wasiSdk) && Directory.Exists(wasiSdk),
            "No WASI SDK in WASI_SDK_PATH (.NET 10's WASI build needs wasi-sdk 25.0)");

        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasiApp", "WasiApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string bundle = Repo.TestProjectPath("WasiApp", "bin", "Release", "net10.0", "wasi-wasm", "AppBundle");
        Assert.True(Directory.Exists(bundle), $"No AppBundle after the build: {build.Tail()}");
        CommandResult run = Command.Run("wasmtime", ["run", "-S", "http", "--dir", ".", "WasiApp.wasm"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("wasi-wasm 42 42 112 42 42 42 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// Without .NET's native WebAssembly build for the app's target framework, a .NET app that
    /// links asm code stops with ASM0054 and the fix, from project references (the consumer
    /// targets) and from a package (the package's props), instead of building without the asm
    /// code: .NET imports none of its WebAssembly build targets then, ignores NativeFileReference,
    /// and the app would fail at its first call (measured on GitHub's windows-2025 image, CI run
    /// 45275929352). The test turns .NET's native build off with the very switch its imports
    /// test, so it runs on every machine, with the workloads or without; the fix the error names
    /// depends on the SDK running it.
    /// </summary>
    /// <param name="app">The app (tests/projects/&lt;app&gt;/&lt;app&gt;.csproj).</param>
    /// <param name="importSwitch">.NET's import switch for the app's runtime: UsingBrowserRuntimeWorkload or UsingWasiRuntimeWorkload.</param>
    /// <param name="workload">The workload of the SDK's own .NET for that runtime.</param>
    /// <remarks>
    /// Not .NET's -p:WasmNativeWorkload=false: it turns WasmNativeWorkloadAvailable off, but an
    /// AppBundle app such as WasmBrowserApp is one .NET marks as needing the native build
    /// (_WasmNativeWorkloadNeeded, from WasmGenerateAppBundle, which the installed workload's
    /// props set), and then UsingBrowserRuntimeWorkload stays true and Emscripten still links
    /// (measured with SDK 10.0.404 and wasm-tools: the build succeeded). The import switch as a
    /// global property is what a machine without the workload has: .NET imports none of its
    /// WebAssembly build targets.
    /// </remarks>
    [Theory]
    [InlineData("WasmBrowserApp", "UsingBrowserRuntimeWorkload", "wasm-tools")]
    [InlineData("WasmBrowserPackageApp", "UsingBrowserRuntimeWorkload", "wasm-tools")]
    [InlineData("WasiApp", "UsingWasiRuntimeWorkload", "wasi-experimental")]
    public void A_wasm_dotnet_app_without_the_native_build_stops_with_ASM0054(string app, string importSwitch, string workload)
    {
        if (app == "WasmBrowserPackageApp")
        {
            CommandResult pack = PackWasmLibrary();
            Assert.True(pack.ExitCode == 0, pack.Tail());
        }

        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath(app, app + ".csproj"), "-c", "Release", $"-p:{importSwitch}=false");
        Assert.True(build.ExitCode != 0, $"{app} built without .NET's native WebAssembly build: {build.Tail()}");
        Assert.Contains("ASM0054", build.Output);
        // The fix by SDK: the app is net10.0, so SDK 10 names the workload itself, and a later SDK
        // the -net10 workload (browser) or a .NET 10 SDK (WASI, whose later-SDK build serves only
        // its own .NET).
        string sdkVersion = sdk.Once("dotnet --version", () => sdk.DotnetCommand("--version")).StdOut.Trim();
        string fix = sdkVersion.StartsWith("10.", StringComparison.Ordinal) ? $"dotnet workload install {workload}"
            : workload == "wasm-tools" ? $"dotnet workload install {workload}-net10"
            : $"build it with a .NET 10 SDK (global.json) and its {workload} workload";
        Assert.Contains(fix, build.Output);
    }

    /// <summary>
    /// A library of native and WebAssembly RIDs with PROTO lines in its native sources builds,
    /// and its API file (the C# bindings' input) describes the RIDs that can declare: the SDK
    /// reads the records from COFF, ELF and Mach-O objects only, so the browser-wasm build reads
    /// none (it used to stop on its wasm objects with ASM0135) and the merge leaves it out (it
    /// would have found add_ints missing there, ASM0133).
    /// </summary>
    [Fact]
    public void A_library_of_native_and_wasm_rids_merges_the_api_of_its_native_rids()
    {
        Build("WasmApi");
        string merged = Repo.TestProjectPath("WasmApi", "bin", "Release", "freestanding", "wasmapi.api.json");
        Assert.True(File.Exists(merged), "No merged API file");
        Assert.Contains("\"add_ints\"", File.ReadAllText(merged), StringComparison.Ordinal);
        Assert.True(File.Exists(Output("WasmApi", "linux-x64", "wasmapi.api.json")), "linux-x64 wrote no API file");
        Assert.False(File.Exists(Output("WasmApi", "browser-wasm", "wasmapi.api.json")), "browser-wasm wrote an API file");
        // The module still exports the function: only the bindings' records are native-only.
        Assert.Contains("add_ints", WasmBinary.Read(Output("WasmApi", "browser-wasm", "wasmapi.wasm")).ExportedFunctions);
    }

    /// <summary>
    /// A PROTO line in a WebAssembly source stops the build with the reason (asmsdk-wasm.inc's
    /// .error), instead of llvm-mc's "invalid instruction" or a line left unchecked: WebAssembly
    /// RIDs declare nothing to the bindings yet. llvm-mc reports an .error of a macro at
    /// "&lt;instantiation&gt;" (the macro's expansion) and names the source line in the note that
    /// follows, "while in macro instantiation", which the line-marker wrapper makes the source's
    /// own path and line (measured with llvm-mc 23.1.5; every GNU macro's .error is reported so).
    /// </summary>
    [Fact]
    public void A_proto_line_in_a_wasm_source_stops_the_build()
    {
        CommandResult build = sdk.BuildTestProject("WasmProto");
        Assert.True(build.ExitCode != 0, $"WasmProto built: {build.Tail()}");
        Assert.Contains("PROTO is not read from WebAssembly sources yet", build.StdOut, StringComparison.Ordinal);
        Assert.Contains("proto.wasm.s:3:1: note: while in macro instantiation", build.StdOut, StringComparison.Ordinal);
    }
}
