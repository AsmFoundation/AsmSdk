using System.Text;
using System.Text.Json;
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
/// links WasmLibrary's archive), WasmExports (an exports file), WasmBrowserApp, WasmWebSdkApp,
/// WasiApp and WasmBrowserPackageApp (.NET apps that link them: WasmWebSdkApp is the
/// Microsoft.NET.Sdk.WebAssembly shape of WasmBrowserApp), and the rule projects WasmNasm,
/// WasmNoSource, WasmConstant, WasmLibc and WasmRules, which must fail with their diagnostics.
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
    /// <c>ASMSDK_TEST_WASM_APPS</c>: <c>auto</c> (the default: run, or skip with the reason),
    /// <c>required</c> (fail instead of skipping, for both runtimes), <c>none</c> (skip), or the
    /// RIDs whose app tests are required, separated by <c>;</c> or <c>,</c>, the others staying
    /// <c>auto</c>. CI's Linux x64 and Windows x64 runners say <c>required</c>: each installs a
    /// .NET 10 SDK of its own with both workloads. A RID list suits a machine that can link one
    /// runtime's apps only, such as SDK 11.0.100, which gives a net10.0 WASI app no native build.
    /// </summary>
    /// <exception cref="InvalidOperationException">ASMSDK_TEST_WASM_APPS names something else (thrown by the initializer).</exception>
    private static readonly (bool None, HashSet<string> Required) WasmApps = ParseWasmApps(Environment.GetEnvironmentVariable("ASMSDK_TEST_WASM_APPS"));

    /// <summary>Reads ASMSDK_TEST_WASM_APPS (see <see cref="WasmApps"/>).</summary>
    /// <param name="value">The variable's value, or null when unset.</param>
    /// <returns>Whether every app test is skipped, and the RIDs whose app tests must run.</returns>
    /// <exception cref="InvalidOperationException">The value is no mode and names a RID other than browser-wasm and wasi-wasm.</exception>
    private static (bool None, HashSet<string> Required) ParseWasmApps(string? value)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        switch (value)
        {
            case null or "" or "auto":
                return (false, required);
            case "none":
                return (true, required);
            case "required":
                return (false, ["browser-wasm", "wasi-wasm"]);
        }

        foreach (string rid in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rid is not ("browser-wasm" or "wasi-wasm"))
            {
                throw new InvalidOperationException($"ASMSDK_TEST_WASM_APPS={value}: expected auto, required, none, or the RIDs browser-wasm and wasi-wasm");
            }

            required.Add(rid);
        }

        return (false, required);
    }

    /// <summary>
    /// Lets a .NET WebAssembly app test go on when a prerequisite holds; otherwise skips it with
    /// the reason, or fails it when the run is required for its RID (<see cref="WasmApps"/>).
    /// </summary>
    /// <param name="holds">Whether the prerequisite is met.</param>
    /// <param name="reason">What is missing, for the skip or failure message.</param>
    /// <param name="rid">The app's runtime: browser-wasm or wasi-wasm.</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the run is required.</exception>
    internal static void RequireForWasmApps(bool holds, string reason, string rid)
    {
        if (WasmApps.None)
        {
            Assert.Skip("ASMSDK_TEST_WASM_APPS=none");
        }

        if (holds)
        {
            return;
        }

        if (WasmApps.Required.Contains(rid))
        {
            Assert.Fail($"{reason} (ASMSDK_TEST_WASM_APPS requires the {rid} app tests)");
        }

        Assert.Skip(reason);
    }

    /// <summary>
    /// Requires .NET's native WebAssembly build for a .NET app, as .NET itself decides it for the
    /// app's target framework and RID (see <see cref="RequireForWasmApps"/>).
    /// </summary>
    /// <param name="sdk">The SDK under test, whose dotnet and environment evaluate the app.</param>
    /// <param name="project">The app's project file.</param>
    /// <param name="property">UsingBrowserRuntimeWorkload (browser-wasm) or UsingWasiRuntimeWorkload (wasi-wasm).</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the run is required.</exception>
    /// <remarks>
    /// The question is .NET's, not a workload list's. A net10.0 app needs the .NET 10 SDK's
    /// wasm-tools, and GitHub's windows-2025 image has SDK 11.0.100 with Visual Studio's
    /// wasm-tools, which serves net11.0 only: a name check passed there, and the app built without
    /// its AppBundle (CI run 45275929352). SDK 11.0.100's wasm-tools-net10 turns the switch on but
    /// does not build the app either (CI run 45351481101), so CI's Windows x64 runner installs a
    /// .NET 10 SDK of its own. The property is the switch .NET's own imports test, and the one
    /// the SDK's ASM0054 check reads, so these tests run exactly where the apps can be linked.
    /// The switch is on whenever the SDK matches the app's .NET version, installed workload or
    /// not, so .NET's own MissingWorkloadPack items count too: a .NET 10 SDK without wasm-tools
    /// answered "true", and every browser app test then failed with NETSDK1147 instead of
    /// skipping (measured with SDK 10.0.101 installed alone, nine tests). Evaluation only, once per
    /// project.
    /// </remarks>
    internal static void RequireWasmNativeBuild(SdkUnderTest sdk, string project, string property)
    {
        string rid = property == "UsingWasiRuntimeWorkload" ? "wasi-wasm" : "browser-wasm";
        if (WasmApps.None)
        {
            Assert.Skip("ASMSDK_TEST_WASM_APPS=none");
        }

        CommandResult answer = sdk.Once($"{project} {property} MissingWorkloadPack", () => sdk.DotnetCommand("msbuild", project, $"-getProperty:{property}", "-getItem:MissingWorkloadPack", "-nologo"));
        CommandResult version = sdk.Once("dotnet --version", () => sdk.DotnetCommand("--version"));
        (string value, string[] missing) = ReadWorkloadAnswer(answer, property);
        RequireForWasmApps(answer.ExitCode == 0 && value == "true" && missing.Length == 0,
            $"The dotnet on PATH ({version.StdOut.Trim()}) has no native WebAssembly build for {Path.GetFileName(project)}: {property} is '{value}'" +
            (missing.Length == 0 ? "" : $", missing workload packs {string.Join(", ", missing)}") +
            $" (exit {answer.ExitCode}). It needs the wasm-tools workload for browser-wasm, or wasi-experimental and WASI_SDK_PATH for wasi-wasm, " +
            "of the app's .NET version: for these net10.0 apps, a .NET 10 SDK",
            rid);
    }

    /// <summary>
    /// Reads dotnet msbuild's answer to -getProperty and -getItem together: a JSON object with
    /// the property under "Properties" and the items under "Items".
    /// </summary>
    /// <param name="answer">The command's result.</param>
    /// <param name="property">The property asked for.</param>
    /// <returns>
    /// The property's value and the MissingWorkloadPack items' names; an empty value and no items
    /// when the command failed or printed no such object (the caller's message then shows its
    /// exit code, and the run skips or fails as for a value other than "true").
    /// </returns>
    private static (string Value, string[] Missing) ReadWorkloadAnswer(CommandResult answer, string property)
    {
        // The object is all the command prints when it succeeds; anything before its first brace
        // (a notice of a preview SDK, say) is not part of it.
        string output = answer.StdOut;
        int start = output.IndexOf('{');
        if (answer.ExitCode != 0 || start < 0)
        {
            return ("", []);
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(output[start..]);
            string value = json.RootElement.TryGetProperty("Properties", out JsonElement properties) && properties.TryGetProperty(property, out JsonElement found)
                ? found.GetString() ?? ""
                : "";
            string[] missing = json.RootElement.TryGetProperty("Items", out JsonElement items) && items.TryGetProperty("MissingWorkloadPack", out JsonElement packs)
                ? packs.EnumerateArray().Select(pack => pack.TryGetProperty("Identity", out JsonElement name) ? name.GetString() ?? "?" : "?").ToArray()
                : [];
            return (value, missing);
        }
        catch (JsonException)
        {
            // Not the object -getProperty/-getItem print: as if nothing was answered.
            return ("", []);
        }
    }

    /// <summary>
    /// Requires that .NET itself not stop every build of a browser app for workload packs it
    /// wants and lacks: a .NET SDK of the app's own .NET version without the wasm-tools workload
    /// lists them as MissingWorkloadPack items, and its _CheckForMissingWorkload fails the first
    /// target with NETSDK1147, before any target a test reads (measured with SDK 10.0.101 alone).
    /// Elsewhere such an app evaluates and runs its early targets without the workload.
    /// </summary>
    /// <param name="app">The browser app's project name (tests/projects/&lt;app&gt;/&lt;app&gt;.csproj).</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the run is required.</exception>
    /// <remarks>
    /// The same evaluation, and the same cached answer, as <see cref="RequireWasmNativeBuild(SdkUnderTest, string, string)"/>,
    /// whose switch does not matter here: only the missing packs do.
    /// </remarks>
    private void RequireNoMissingWorkloadPacks(string app)
    {
        const string Property = "UsingBrowserRuntimeWorkload";
        string project = Repo.TestProjectPath(app, app + ".csproj");
        CommandResult answer = sdk.Once($"{project} {Property} MissingWorkloadPack", () => sdk.DotnetCommand("msbuild", project, $"-getProperty:{Property}", "-getItem:MissingWorkloadPack", "-nologo"));
        (_, string[] missing) = ReadWorkloadAnswer(answer, Property);
        RequireForWasmApps(missing.Length == 0,
            $"The dotnet on PATH wants the workload packs {string.Join(", ", missing)} for {app} and does not have them: .NET stops every build of it (NETSDK1147). Install the wasm-tools workload of the app's .NET version",
            "browser-wasm");
    }

    /// <summary>Requires .NET's native WebAssembly build for a .NET app of tests/projects (see the static overload).</summary>
    /// <param name="app">The app's project name (tests/projects/&lt;app&gt;/&lt;app&gt;.csproj).</param>
    /// <param name="property">UsingBrowserRuntimeWorkload (browser-wasm) or UsingWasiRuntimeWorkload (wasi-wasm).</param>
    /// <exception cref="Exception">Xunit's skip exception; an assertion failure when the run is required.</exception>
    private void RequireWasmNativeBuild(string app, string property) =>
        RequireWasmNativeBuild(sdk, Repo.TestProjectPath(app, app + ".csproj"), property);

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
    /// A launch profile's variables reach a WASI program dotnet run starts in wasmtime, which passes
    /// a program only the variables it is told to: the SDK names every profile's variables
    /// (--env NAME), and wasmtime takes their values from its own environment, where the CLI put
    /// the running profile's. Without a profile the names pass nothing. WasiEcho prints its
    /// arguments and its environment; its launch settings hold comments and trailing commas,
    /// which the CLI accepts and so the SDK must: outside the profile the test runs (the .NET
    /// 10.0.4xx CLI drops a profile with either inside it) and inside the Lenient profile, whose
    /// variable's name the SDK must still read. Any SDK: the names come from the file.
    /// </summary>
    [Fact]
    public void Dotnet_run_passes_a_launch_profiles_variables_to_a_wasi_program()
    {
        RequireWasmtime();
        CommandResult profile = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release"]);
        Assert.True(profile.ExitCode == 0, $"exit {profile.ExitCode}: {profile.Tail()}");
        Assert.Contains("ASM_PROBE=from-profile", profile.StdOut, StringComparison.Ordinal);
        Assert.Contains("OTHER=o", profile.StdOut, StringComparison.Ordinal);

        CommandResult none = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release", "--no-launch-profile"], "x y");
        Assert.True(none.ExitCode == 0, $"exit {none.ExitCode}: {none.Tail()}");
        Assert.Contains("x y", none.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("ASM_PROBE=", none.StdOut, StringComparison.Ordinal);

        // ASM_LENIENT is named only in the Lenient profile, between a comment and trailing
        // commas. Set in dotnet run's own environment, as a user's shell sets it, it reaches the
        // program only if the SDK read that profile and named it to wasmtime, which passes
        // nothing else (the WasiEcho profile, not the Lenient one, is the one that runs).
        CommandResult lenient = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release"], new Dictionary<string, string?> { ["ASM_LENIENT"] = "from-environment" });
        Assert.True(lenient.ExitCode == 0, $"exit {lenient.ExitCode}: {lenient.Tail()}");
        Assert.Contains("ASM_LENIENT=from-environment", Lines(lenient));
    }

    /// <summary>
    /// dotnet run -e reaches a WASI program in wasmtime: from SDK 10.0.300 on, the CLI passes -e
    /// to the build as RuntimeEnvironmentVariable items, which the SDK names to wasmtime
    /// (--env NAME). ASM_E_ONLY, because no WasiEcho profile names it: a name the launch settings
    /// file lists (ASM_PROBE) reaches wasmtime even without -e's path, and would hide a broken one.
    /// Skipped before SDK 10.0.300, whose dotnet run passes -e to no build: there no SDK can know
    /// the names.
    /// </summary>
    [Fact]
    public void Dotnet_run_passes_e_variables_to_a_wasi_program()
    {
        RequireWasmtime();
        if (!sdk.DotnetRunPassesEnvironmentToBuild())
        {
            Assert.Skip($"SDK {sdk.SdkVersion()}'s dotnet run passes -e to no build, so the SDK cannot name the variables to wasmtime (it can from SDK 10.0.300 on)");
        }

        CommandResult run = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release", "--no-launch-profile", "-e", "ASM_E_ONLY=from-e"], "z");
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Tail()}");
        Assert.Contains("ASM_E_ONLY=from-e", Lines(run));
    }

    /// <summary>The lines a WasiEcho run printed (its arguments, then its environment), without line ends.</summary>
    /// <param name="run">The dotnet run result.</param>
    /// <returns>The lines of standard output.</returns>
    private static string[] Lines(CommandResult run) => run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    /// <summary>
    /// A launch profile's commandLineArgs reach a WASI program dotnet run starts in wasmtime. The
    /// CLI applies them only to a run command without arguments of its own, and wasmtime's always
    /// has some, so the SDK starts a launcher instead (obj/&lt;Configuration&gt;/&lt;tfm&gt;/asm-run/: an
    /// x64 program it builds on Windows, a shell script elsewhere), which runs wasmtime with what
    /// the CLI hands it. Arguments after the double dash replace the profile's, as the CLI does
    /// for every project; --no-launch-profile passes neither. Any SDK.
    /// </summary>
    [Fact]
    public void A_launch_profiles_arguments_reach_a_wasi_program()
    {
        RequireWasmtime();
        CommandResult profile = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release"]);
        Assert.True(profile.ExitCode == 0, $"exit {profile.ExitCode}: {profile.Tail()}");
        string[] lines = Lines(profile);
        Assert.Contains("profile-arg", lines);
        Assert.Contains("two words", lines);
        Assert.Contains("ASM_PROBE=from-profile", lines);
        string launcher = Repo.TestProjectPath("WasiEcho", "obj", "Release", "freestanding", "asm-run", OperatingSystem.IsWindows() ? "WasiEcho-run.exe" : "WasiEcho-run.sh");
        Assert.True(File.Exists(launcher), $"no launcher at {launcher}");

        CommandResult replaced = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release"], "after the dash");
        Assert.True(replaced.ExitCode == 0, $"exit {replaced.ExitCode}: {replaced.Tail()}");
        Assert.Contains("after the dash", Lines(replaced));
        Assert.DoesNotContain("profile-arg", Lines(replaced));

        CommandResult none = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release", "--no-launch-profile"]);
        Assert.True(none.ExitCode == 0, $"exit {none.ExitCode}: {none.Tail()}");
        Assert.DoesNotContain("profile-arg", Lines(none));
    }

    /// <summary>
    /// A project's own RunArguments reach the WASI program through the launcher as .NET splits
    /// them, a single quote, a dollar sign and a backslash included (the shell script must not
    /// read them), and keep a profile's arguments out, as the CLI does for any project that sets
    /// them; arguments after the double dash follow them (WasiEcho with EchoCase=RunArguments).
    /// </summary>
    [Fact]
    public void A_projects_RunArguments_reach_a_wasi_program_and_keep_a_profiles_out()
    {
        RequireWasmtime();
        CommandResult run = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release", "-p:EchoCase=RunArguments"]);
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Tail()}");
        string[] lines = Lines(run);
        int first = Array.IndexOf(lines, "run arg");
        Assert.True(first >= 0, $"no \"run arg\" line: {run.Tail()}");
        Assert.Equal(["run arg", "'single'", "$HOME", "back\\slash"], lines.Skip(first).Take(4).ToArray());
        Assert.DoesNotContain("profile-arg", lines);
        Assert.Contains("ASM_PROBE=from-profile", lines);

        CommandResult appended = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release", "-p:EchoCase=RunArguments"], "extra");
        Assert.True(appended.ExitCode == 0, $"exit {appended.ExitCode}: {appended.Tail()}");
        string[] appendedLines = Lines(appended);
        int start = Array.IndexOf(appendedLines, "run arg");
        Assert.True(start >= 0, $"no \"run arg\" line: {appended.Tail()}");
        Assert.Equal(["run arg", "'single'", "$HOME", "back\\slash", "extra"], appendedLines.Skip(start).Take(5).ToArray());
    }

    /// <summary>
    /// Stopping dotnet run's Windows launcher stops the program it started, the three ways a run
    /// is stopped: Ctrl+C in the console (a user), Ctrl+C to a new process group (dotnet watch,
    /// which starts the app in a group of its own: Windows then sets the inherited "ignore
    /// Ctrl+C" flag, which the launcher turns off again), and the launcher killed (dotnet watch
    /// when its Ctrl+C times out, an IDE when it stops a run: the program runs in the launcher's
    /// kill-on-close job). With Ctrl+C the launcher passes on the program's exit code
    /// (STATUS_CONTROL_C_EXIT). The launcher survives Ctrl+C by a handler routine of its own:
    /// the ignore flag of SetConsoleCtrlHandler(NULL, TRUE), which its first version set, is
    /// inherited by child processes, and wasmtime, WSL or Docker then ignored Ctrl+C (measured
    /// with ping -t: it ran on; and without the job, a killed launcher left it running).
    /// tests/projects/CtrlC/ctrlc.cs stops the run from a process of its own (it joins the
    /// launcher's console), on a copy of the launcher dotnet run built for WasiEcho, whose
    /// launch file runs ping -t instead. Windows only: elsewhere the launcher is a shell script
    /// that execs the command, which then gets the signals itself.
    /// </summary>
    /// <param name="mode">ctrl-c, group-ctrl-c or kill (see ctrlc.cs).</param>
    [Theory]
    [InlineData("ctrl-c")]
    [InlineData("group-ctrl-c")]
    [InlineData("kill")]
    public void Stopping_the_launcher_stops_the_program(string mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The launcher is a program on Windows only; elsewhere a shell script execs the command");
        }

        RequireWasmtime();
        CommandResult run = sdk.DotnetRun(Repo.TestProject("WasiEcho"), ["-c", "Release"]);
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}: {run.Tail()}");

        // A copy, so that the probe's launch file never replaces the one dotnet run wrote.
        string folder = Path.Combine(Path.GetTempPath(), "asmsdk-ctrlc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        CommandResult probe;
        try
        {
            string launcher = Path.Combine(folder, "ctrlc-run.exe");
            File.Copy(Repo.TestProjectPath("WasiEcho", "obj", "Release", "freestanding", "asm-run", "WasiEcho-run.exe"), launcher);
            // The test environment (no MSBuild variables of the test host's own dotnet test);
            // started in the copy's folder, which no global.json pins.
            probe = Command.Run("dotnet", ["run", Repo.TestProjectPath("CtrlC", "ctrlc.cs"), "--", mode, launcher], folder, sdk.TestEnvironment);
        }
        finally
        {
            DeleteBestEffort(folder);
        }

        // Checked after the cleanup, which can therefore never hide the probe's verdict.
        Assert.True(probe.ExitCode == 0, $"probe exit {probe.ExitCode} (1: the program ran on; 2: the probe could not run): {probe.Tail()}");
        if (mode != "kill")
        {
            Assert.Contains("exited 0xC000013A", probe.StdOut, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Deletes a temporary folder, retrying for up to ten seconds and then giving up quietly: a
    /// program that has just ended can keep its file open a moment longer (TerminateProcess
    /// returns before the process is gone, and Windows on Arm's x64 emulation reads an x64 program
    /// it ran for its translation cache). On the win-arm64 runner Directory.Delete threw "Access
    /// to the path 'ctrlc-run.exe' is denied" from a finally block, which replaced the test's own
    /// result. A folder left in the temporary directory harms nothing.
    /// </summary>
    /// <param name="folder">The folder to delete; nothing happens when it does not exist.</param>
    private static void DeleteBestEffort(string folder)
    {
        for (int attempt = 0; attempt < 40 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use: the file is let go within a moment once its process is gone.
                Thread.Sleep(250);
            }
        }
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
    /// The debug level a browser app's runtime starts with: the "debugLevel" of the boot
    /// configuration .NET wrote, in dotnet.boot.js (an app made with Microsoft.NET.Sdk) or inside
    /// dotnet.js (Microsoft.NET.Sdk.WebAssembly).
    /// </summary>
    /// <param name="bootFile">The file that holds the boot configuration.</param>
    /// <returns>The value, as written (0, -1).</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The file has no debugLevel.</exception>
    private static string BootDebugLevel(string bootFile)
    {
        System.Text.RegularExpressions.Match level =
            System.Text.RegularExpressions.Regex.Match(File.ReadAllText(bootFile), "\"debugLevel\":\\s*(-?\\d+)");
        Assert.True(level.Success, $"No debugLevel in {bootFile}");
        return level.Groups[1].Value;
    }

    /// <summary>
    /// The four browser apps, by how they get asm code and what they are made with: WasmBrowserApp
    /// (project references, Microsoft.NET.Sdk), WasmWebSdkApp (project references,
    /// Microsoft.NET.Sdk.WebAssembly), WasmBrowserPackageApp and WasmWebSdkPackageApp (the same two
    /// shapes with the package Test.WasmLibrary, whose props stand in for the consumer targets).
    /// </summary>
    public static TheoryData<string> BrowserApps => new() { "WasmBrowserApp", "WasmWebSdkApp", "WasmBrowserPackageApp", "WasmWebSdkPackageApp" };

    /// <summary>Packs Test.WasmLibrary first when the app takes it as a package (its name says Package).</summary>
    /// <param name="app">The browser app.</param>
    private void PackWasmLibraryFor(string app)
    {
        if (app.Contains("Package", StringComparison.Ordinal))
        {
            CommandResult pack = PackWasmLibrary();
            Assert.True(pack.ExitCode == 0, pack.Tail());
        }
    }

    /// <summary>
    /// A .NET browser-wasm app links a library, a static library and object files from asm
    /// projects into its runtime and calls each through P/Invoke, under Node: the consumer
    /// targets hand them over as NativeFileReference, with the projects' AssemblyNames as the
    /// P/Invoke modules (an object file's own name would be "sub.wasm"). Its Release build's boot
    /// configuration says debugLevel 0, the SDK's default (.NET's own is -1 in every build).
    /// </summary>
    [Fact]
    public void A_browser_wasm_dotnet_app_links_asm_projects()
    {
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        RequireWasmNativeBuild("WasmBrowserApp", "UsingBrowserRuntimeWorkload");
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmBrowserApp", "WasmBrowserApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string bundle = Repo.TestProjectPath("WasmBrowserApp", "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
        // Said here rather than by the process start ("The directory name is invalid"): a build
        // without .NET's native WebAssembly build succeeds without a bundle.
        Assert.True(Directory.Exists(bundle), $"No AppBundle after the build: {build.Tail()}");
        Assert.Equal("0", BootDebugLevel(Path.Combine(bundle, "_framework", "dotnet.boot.js")));
        CommandResult run = Command.Run("node", ["main.mjs"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("browser-wasm 42 42 111 42 42 42 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// The same app made with .NET's WebAssembly SDK (WasmWebSdkApp, Microsoft.NET.Sdk.WebAssembly:
    /// the wasmbrowser template's and Blazor WebAssembly's shape) links the same three into its
    /// runtime and gives the same answer, under Node through run.mjs, from the build's wwwroot
    /// folder, where this shape writes the app (fingerprinted files in _framework, the boot
    /// configuration inside dotnet.js, which says debugLevel 0 in Release here too).
    /// </summary>
    [Fact]
    public void A_webassembly_sdk_app_links_asm_projects()
    {
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        RequireWasmNativeBuild("WasmWebSdkApp", "UsingBrowserRuntimeWorkload");
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmWebSdkApp", "WasmWebSdkApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string wwwroot = Repo.TestProjectPath("WasmWebSdkApp", "bin", "Release", "net10.0", "wwwroot");
        string dotnetJs = Path.Combine(wwwroot, "_framework", "dotnet.js");
        Assert.True(File.Exists(dotnetJs), $"No _framework/dotnet.js after the build: {build.Tail()}");
        Assert.Equal("0", BootDebugLevel(dotnetJs));
        CommandResult run = Command.Run("node", ["run.mjs", wwwroot], Repo.TestProjectPath("WasmWebSdkApp"));
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("browser-wasm 42 42 111 42 42 42 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// The SDK's debug level for a browser app's BUILD (the owner's choice of 2026-11-25): 0 in
    /// Release, -1 in Debug, nothing in a publish (dotnet publish's _IsPublishing, Visual Studio's
    /// DeployOnBuild, the nested publish build), so that a publish keeps .NET's own 0, and nothing
    /// in another configuration. For project references the consumer targets set it at
    /// evaluation, for a package its props in a target before PrepareForBuild: the value is read
    /// after that target ran (-getProperty with -t), which .NET's boot configuration tasks, run
    /// later, see too. Evaluation and one early target only: the apps with project references run
    /// on every machine, with the workloads or without, unless .NET itself stops them for
    /// workload packs it wants and lacks (<see cref="RequireNoMissingWorkloadPacks"/>). The package
    /// apps need .NET's native WebAssembly build, because their props' ASM0054 check runs before
    /// PrepareForBuild as well and stops them without it (measured with SDK 11.0.100 and no
    /// workload).
    /// </summary>
    /// <param name="app">The browser app (<see cref="BrowserApps"/>).</param>
    [Theory]
    [MemberData(nameof(BrowserApps))]
    public void A_browser_app_build_gets_the_sdk_debug_level(string app)
    {
        if (app.Contains("Package", StringComparison.Ordinal))
        {
            RequireWasmNativeBuild(app, "UsingBrowserRuntimeWorkload");
        }
        else
        {
            RequireNoMissingWorkloadPacks(app);
        }

        PackWasmLibraryFor(app);
        string project = Repo.TestProjectPath(app, app + ".csproj");
        (string Arguments, string Expected)[] cases =
        [
            ("-p:Configuration=Release", "0"),
            ("-p:Configuration=Debug", "-1"),
            ("-p:Configuration=Release -p:_IsPublishing=true", ""),
            ("-p:Configuration=Debug -p:DeployOnBuild=true", ""),
            ("-p:Configuration=Release -p:WasmBuildingForNestedPublish=true", ""),
            ("-p:Configuration=Staging", ""),
        ];
        foreach ((string arguments, string expected) in cases)
        {
            CommandResult level = sdk.Dotnet(["msbuild", project, "-restore", "-t:PrepareForBuild", "-getProperty:WasmDebugLevel", .. arguments.Split(' ')]);
            Assert.True(level.ExitCode == 0, $"{app} {arguments}: {level.Output}");
            Assert.True(level.StdOut.Trim() == expected, $"{app} {arguments}: WasmDebugLevel '{level.StdOut.Trim()}', expected '{expected}'");
        }
    }

    /// <summary>
    /// ASM0055 (the owner's choice of 2026-11-25, "Build warning"): a build of a browser app that
    /// links asm code, with RunAOTCompilation=true, warns once that it interprets the C# (.NET
    /// compiles ahead of time only when it publishes), from the consumer targets or a package's
    /// props; a publish, RunAOTCompilationAfterBuild (a build that does compile) and NoWarn say
    /// nothing. The warning comes before anything is compiled, so the build stops after the
    /// references (-t:PrepareForBuild;ResolveProjectReferences). Only where .NET's native
    /// WebAssembly build is: without its workload .NET itself stops a build that sets
    /// RunAOTCompilation (NETSDK1147, "the following workloads must be installed:
    /// wasm-tools-net10", measured with SDK 11.0.100), before any warning of ours.
    /// </summary>
    /// <param name="app">The browser app (<see cref="BrowserApps"/>).</param>
    [Theory]
    [MemberData(nameof(BrowserApps))]
    public void A_browser_app_build_that_asks_for_aot_warns_ASM0055(string app)
    {
        RequireWasmNativeBuild(app, "UsingBrowserRuntimeWorkload");
        PackWasmLibraryFor(app);
        string project = Repo.TestProjectPath(app, app + ".csproj");
        (string Arguments, int Warnings)[] cases =
        [
            ("-p:RunAOTCompilation=true", 1),
            ("-p:RunAOTCompilation=true -p:_IsPublishing=true", 0),
            ("-p:RunAOTCompilation=true -p:RunAOTCompilationAfterBuild=true", 0),
            ("-p:RunAOTCompilation=true -p:NoWarn=ASM0055", 0),
            ("-p:Configuration=Release", 0),
        ];
        foreach ((string arguments, int warnings) in cases)
        {
            CommandResult build = sdk.Dotnet(["msbuild", project, "-restore", "-p:Configuration=Release", "-t:PrepareForBuild;ResolveProjectReferences", .. arguments.Split(' ')]);
            Assert.True(build.ExitCode == 0, $"{app} {arguments}: {build.Tail()}");
            string[] said = build.StdOut.Split('\n').Where(line => line.Contains("warning ASM0055", StringComparison.Ordinal)).ToArray();
            Assert.True(said.Length == warnings, $"{app} {arguments}: {said.Length} ASM0055 warnings, expected {warnings}: {build.Tail()}");
            if (warnings != 0)
            {
                Assert.Contains($"{app} sets RunAOTCompilation, but .NET compiles a WebAssembly app ahead of time only when it publishes it: this build interprets the C#.", said[0]);
            }
        }
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
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        RequireWasmNativeBuild("WasmBrowserPackageApp", "UsingBrowserRuntimeWorkload");
        CommandResult pack = PackWasmLibrary();
        Assert.True(pack.ExitCode == 0, pack.Tail());
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmBrowserPackageApp", "WasmBrowserPackageApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string bundle = Repo.TestProjectPath("WasmBrowserPackageApp", "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
        Assert.True(Directory.Exists(bundle), $"No AppBundle after the build: {build.Tail()}");
        // The package's own target set the SDK's Release debug level: the app has no AsmFoundation.Sdk.
        Assert.Equal("0", BootDebugLevel(Path.Combine(bundle, "_framework", "dotnet.boot.js")));
        CommandResult run = Command.Run("node", ["main.mjs"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("browser-wasm 42 42 111 42 42 42", run.StdOut.Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// The same package in an app made with .NET's WebAssembly SDK (WasmWebSdkPackageApp): the
    /// package's props link its archive into this shape's runtime too, and set the Release debug
    /// level, which this shape writes inside dotnet.js. Run under Node with WasmWebSdkApp's
    /// run.mjs, from the build's wwwroot folder.
    /// </summary>
    [Fact]
    public void A_webassembly_sdk_app_links_an_asm_package()
    {
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        RequireWasmNativeBuild("WasmWebSdkPackageApp", "UsingBrowserRuntimeWorkload");
        PackWasmLibraryFor("WasmWebSdkPackageApp");
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmWebSdkPackageApp", "WasmWebSdkPackageApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string wwwroot = Repo.TestProjectPath("WasmWebSdkPackageApp", "bin", "Release", "net10.0", "wwwroot");
        string dotnetJs = Path.Combine(wwwroot, "_framework", "dotnet.js");
        Assert.True(File.Exists(dotnetJs), $"No _framework/dotnet.js after the build: {build.Tail()}");
        Assert.Equal("0", BootDebugLevel(dotnetJs));
        CommandResult run = Command.Run("node", ["run.mjs", wwwroot], Repo.TestProjectPath("WasmWebSdkApp"));
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
        RequireForWasmApps(Command.OnPath("wasmtime"), "No wasmtime on PATH to run the WASI app here (https://wasmtime.dev)", "wasi-wasm");
        RequireWasmNativeBuild("WasiApp", "UsingWasiRuntimeWorkload");
        string? wasiSdk = Environment.GetEnvironmentVariable("WASI_SDK_PATH");
        RequireForWasmApps(!string.IsNullOrEmpty(wasiSdk) && Directory.Exists(wasiSdk),
            "No WASI SDK in WASI_SDK_PATH (.NET 10's WASI build needs wasi-sdk 25.0)", "wasi-wasm");

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
    /// test, so it runs on every machine, with the workloads or without. The fix the error names
    /// depends on the SDK and, on a later SDK than the app's .NET, on the app's shape (the owner's
    /// choice of 2026-11-25, "Correct the claim"); each app is built twice, on the SDK running the
    /// test and as if on the other kind of SDK (NETCoreSdkVersion, the one property the check
    /// reads for it), so both answers are checked everywhere.
    /// </summary>
    /// <param name="app">The app (tests/projects/&lt;app&gt;/&lt;app&gt;.csproj).</param>
    /// <param name="importSwitch">.NET's import switch for the app's runtime: UsingBrowserRuntimeWorkload or UsingWasiRuntimeWorkload.</param>
    /// <param name="workload">The workload of the SDK's own .NET for that runtime.</param>
    /// <param name="shape">How the app is made: "console" (Microsoft.NET.Sdk with RuntimeIdentifier browser-wasm), "websdk" (Microsoft.NET.Sdk.WebAssembly) or "wasi".</param>
    /// <remarks>
    /// Not .NET's -p:WasmNativeWorkload=false: it turns WasmNativeWorkloadAvailable off, but an
    /// AppBundle app such as WasmBrowserApp is one .NET marks as needing the native build
    /// (_WasmNativeWorkloadNeeded, from WasmGenerateAppBundle, which the installed workload's
    /// props set), and then UsingBrowserRuntimeWorkload stays true and Emscripten still links
    /// (measured with SDK 10.0.404 and wasm-tools: the build succeeded). The import switch as a
    /// global property is what a machine without the workload has: .NET imports none of its
    /// WebAssembly build targets. The later-SDK texts were checked on SDK 11.0.100 itself too
    /// (2026-11-25), word for word what -p:NETCoreSdkVersion=11.0.100 gives on SDK 10.
    /// </remarks>
    [Theory]
    [InlineData("WasmBrowserApp", "UsingBrowserRuntimeWorkload", "wasm-tools", "console")]
    [InlineData("WasmWebSdkApp", "UsingBrowserRuntimeWorkload", "wasm-tools", "websdk")]
    [InlineData("WasmBrowserPackageApp", "UsingBrowserRuntimeWorkload", "wasm-tools", "console")]
    [InlineData("WasmWebSdkPackageApp", "UsingBrowserRuntimeWorkload", "wasm-tools", "websdk")]
    [InlineData("WasiApp", "UsingWasiRuntimeWorkload", "wasi-experimental", "wasi")]
    public void A_wasm_dotnet_app_without_the_native_build_stops_with_ASM0054(string app, string importSwitch, string workload, string shape)
    {
        PackWasmLibraryFor(app);
        string sdkVersion = sdk.Once("dotnet --version", () => sdk.DotnetCommand("--version")).StdOut.Trim();
        bool runningSdkIsTheApps = sdkVersion.StartsWith("10.", StringComparison.Ordinal);
        // The SDK running the test, then the other kind: a .NET 10 SDK for these net10.0 apps
        // names the workload itself; a later one names the fix of the app's shape.
        foreach ((string extra, bool laterSdk) in new[]
                 {
                     ("", !runningSdkIsTheApps),
                     (runningSdkIsTheApps ? "-p:NETCoreSdkVersion=11.0.100" : "-p:NETCoreSdkVersion=10.0.100", runningSdkIsTheApps),
                 })
        {
            string[] arguments = ["build", Repo.TestProjectPath(app, app + ".csproj"), "-c", "Release", $"-p:{importSwitch}=false"];
            CommandResult build = sdk.Dotnet(extra.Length == 0 ? arguments : [.. arguments, extra]);
            Assert.True(build.ExitCode != 0, $"{app} {extra} built without .NET's native WebAssembly build: {build.Tail()}");
            Assert.Contains("ASM0054", build.Output);
            string fix = !laterSdk ? $"dotnet workload install {workload}" : shape switch
            {
                // Measured with SDK 11.0.100 and wasm-tools-net10: this shape builds, asm code included.
                "websdk" => $"dotnet workload install {workload}-net10, or build it with a .NET 10 SDK (global.json) and its {workload} workload",
                // The same SDK did not build this shape, even .NET's own wasmconsole template.
                "console" => $"build it with a .NET 10 SDK (global.json) and its {workload} workload; a later SDK's {workload}-net10 did not build a net10.0 app made with Microsoft.NET.Sdk and RuntimeIdentifier browser-wasm",
                _ => $"build it with a .NET 10 SDK (global.json) and its {workload} workload, since a later SDK's WASI build serves only its own .NET",
            };
            Assert.True(build.Output.Contains(fix, StringComparison.Ordinal), $"{app} {extra}: no \"{fix}\" in {build.Tail()}");
        }
    }

    /// <summary>
    /// A library of native and WebAssembly RIDs, with the same PROTO line in its .asm and its
    /// .wasm.s, has one API (the owner's choice of 2026-11-24, "one API for every RID"): each RID
    /// writes its API file, the wasm one from the records of the object's "asmapi" custom section,
    /// and the merged file holds the function both declare. The module keeps the records ("records
    /// kept in the module"): the shipped .wasm carries the PROTO line and the "fn" record with the
    /// function's wasm type, and Node reads them with WebAssembly.Module.customSections, the way
    /// a JavaScript host learns the module's API.
    /// </summary>
    [Fact]
    public void A_library_of_native_and_wasm_rids_has_one_api()
    {
        Build("WasmApi");
        string merged = Repo.TestProjectPath("WasmApi", "bin", "Release", "freestanding", "wasmapi.api.json");
        Assert.True(File.Exists(merged), "No merged API file");
        Assert.Contains("\"prototype\": \"int32_t add_ints(int32_t a, int32_t b)\"", File.ReadAllText(merged), StringComparison.Ordinal);
        Assert.True(File.Exists(Output("WasmApi", "linux-x64", "wasmapi.api.json")), "linux-x64 wrote no API file");
        string wasmApi = Output("WasmApi", "browser-wasm", "wasmapi.api.json");
        Assert.True(File.Exists(wasmApi), "browser-wasm wrote no API file");
        Assert.Contains("\"runtimeIdentifier\": \"browser-wasm\"", File.ReadAllText(wasmApi), StringComparison.Ordinal);

        string module = Output("WasmApi", "browser-wasm", "wasmapi.wasm");
        Assert.Contains("add_ints", WasmBinary.Read(module).ExportedFunctions);
        Assert.Equal(new[] { "proto int32_t add_ints(int32_t a, int32_t b)", "fn 1 add_ints (i32, i32) -> (i32)" }, WasmBinary.Read(module).CustomRecords("asmapi"));

        RequireNode();
        string script = "const m = new WebAssembly.Module(require('fs').readFileSync(process.argv[1]));"
                        + "for (const s of WebAssembly.Module.customSections(m, 'asmapi'))"
                        + " console.log(new TextDecoder().decode(s).split('\\0').filter(r => r).join(' | '));";
        CommandResult node = Command.Run("node", ["-e", script, module], Repo.Root);
        Assert.True(node.ExitCode == 0, node.Output);
        Assert.Equal("proto int32_t add_ints(int32_t a, int32_t b) | fn 1 add_ints (i32, i32) -> (i32)", node.StdOut.Trim());
    }

    /// <summary>
    /// A PROTO line inside a function body stops the build with the guard's message. llvm-mc's
    /// wasm parser cannot return to the section a record leaves it in, so the record would take
    /// the rest of the body with it (measured: the module linked, and the function had lost its
    /// end). llvm-mc reports an .error of a macro at "&lt;instantiation&gt;" and names the source line
    /// in the note that follows, "while in macro instantiation", which the line-marker wrapper
    /// makes the source's own path and line (measured with llvm-mc 23.1.5).
    /// </summary>
    [Fact]
    public void A_proto_inside_a_wasm_function_stops_the_build()
    {
        CommandResult build = sdk.BuildTestProject("WasmProto");
        Assert.True(build.ExitCode != 0, $"WasmProto built: {build.Tail()}");
        Assert.Contains("PROTO and the function macros go between functions: answer has no ENDFUNCTION yet", build.StdOut, StringComparison.Ordinal);
        Assert.Contains("proto.wasm.s:5:1: note: while in macro instantiation", build.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a WebAssembly PROTO line must agree with stops the build with its code, at the line:
    /// its type lowered to wasm against FUNCTION's whole type (ASM0132, exact where native code
    /// compares a count), a function no module exports (ASM0131), and a native RID that declares
    /// what the wasm RID does not (ASM0133: one API for every RID, WebAssembly included).
    /// </summary>
    /// <param name="apiCase">The case folder of tests/projects/WasmApiErrors.</param>
    /// <param name="code">The diagnostic it must stop with.</param>
    /// <param name="text">A part of the message that names the cause.</param>
    /// <param name="line">The line the diagnostic must point at, printed "file.wasm.s(line)" (the SDK logs no column); 0 for a message that names no source line (the comparison's).</param>
    [Theory]
    [InlineData("signature", "ASM0132", "lowers to the WebAssembly type (i64) -> (i64)", 4)]
    [InlineData("local", "ASM0131", "inner is a LOCAL_FUNCTION", 3)]
    [InlineData("drift", "ASM0133", "browser-wasm: not declared", 0)]
    public void A_wrong_wasm_proto_stops_the_build(string apiCase, string code, string text, int line)
    {
        // A configuration per case keeps each case's outputs apart, as ApiTests does.
        CommandResult build = sdk.Dotnet("build", Repo.TestProject("WasmApiErrors"), "-c", $"Wasm{apiCase}", $"-p:ApiCase={apiCase}");
        Assert.True(build.ExitCode != 0, $"case {apiCase} built: {build.Tail()}");
        Assert.Contains($"error {code}", build.StdOut, StringComparison.Ordinal);
        Assert.Contains(text, build.StdOut, StringComparison.Ordinal);
        if (line > 0)
        {
            Assert.Contains($".wasm.s({line})", build.StdOut, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A .NET browser-wasm app calls an asm library through the C# bindings' baseline alone: the
    /// library's browser-wasm build writes its API file from the wasm records, the consumer
    /// targets write the baseline from it (the app's reference is built for the app's RID), and
    /// Wasmapi.Add.AddInts (the namespace named after the library, the class after add.wasm.s)
    /// links into dotnet.native.wasm with the archive. Before WebAssembly RIDs wrote API files,
    /// such an app got no bindings (3e9a958).
    /// </summary>
    [Fact]
    public void A_browser_wasm_dotnet_app_calls_the_generated_bindings()
    {
        RequireForWasmApps(Command.OnPath("node"), "No Node on PATH to run the browser-wasm app here", "browser-wasm");
        RequireWasmNativeBuild("WasmBindingsApp", "UsingBrowserRuntimeWorkload");
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("WasmBindingsApp", "WasmBindingsApp.csproj"), "-c", "Release");
        Assert.True(build.ExitCode == 0, build.Tail());
        string bundle = Repo.TestProjectPath("WasmBindingsApp", "bin", "Release", "net10.0", "browser-wasm", "AppBundle");
        Assert.True(Directory.Exists(bundle), $"No AppBundle after the build: {build.Tail()}");
        CommandResult run = Command.Run("node", ["main.mjs"], bundle);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("browser-wasm bindings 42", run.StdOut.Split('\n').Select(l => l.TrimEnd('\r')));
    }
}
