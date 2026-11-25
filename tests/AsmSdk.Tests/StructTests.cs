using System.Text.Json;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// STRUCT lines, phase 3 of the generated C# bindings (catalog asmsdk/plans/csharp-bindings.md):
/// the SDK reads each source's STRUCT lines before assembling (the owner's choice of 2026-11-24,
/// because GNU macros cannot split a pointer field), lays every struct out as C does on the RID,
/// gives the source its constants (name.field, name_size), records the layout in the API file,
/// checks that the RIDs agree, and the generator writes a sequential C# struct per STRUCT.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// tests/projects/ApiStruct declares every kind of field the SDK lays out, in NASM (x64) and GNU
/// syntax (ARM64), for all eight RIDs; tests/projects/NetStructs fills each struct in C# and has
/// the asm code read it back, on this machine, so each CI machine proves its own layout.
/// tests/projects/StructErrors and NetStructsErrors hold one wrong case each.
/// </remarks>
public sealed class StructTests(SdkUnderTest sdk)
{
    /// <summary>ApiStruct's structs, sorted by name: size, alignment, canonical declaration and each field's offset.</summary>
    private static readonly (string Name, int Size, int Alignment, string Declaration, int[] Offsets)[] ApiStructStructs =
    [
        ("hash_state", 112, 8, "hash_state, uint32_t h[8], uint64_t length, uint8_t block[64], bool final", [0, 32, 40, 104]),
        ("mixed", 24, 8, "mixed, bool flag, const uint8_t* data, uint32_t count, uint16_t tail, char tag", [0, 8, 16, 20, 22]),
        ("point", 8, 4, "point, int32_t x, int32_t y", [0, 4]),
        ("rect", 20, 4, "rect, point min, point max, uint32_t flags", [0, 8, 16]),
        ("sorter", 24, 8, "sorter, int32_t (*cmp)(int32_t, int32_t), void* context, sorter* next", [0, 8, 16]),
    ];

    /// <summary>
    /// The library's API file (bin/Release/freestanding/apistruct.api.json) lists every struct
    /// once, sorted by name, with C's layout: point and rect come from an include file that both
    /// sources of a dialect include, so their records reach two objects and are still one struct
    /// each. Arrays carry their count, a struct field and a pointer to a struct say "struct", a
    /// function pointer field its signature; a PROTO's pointer to a struct says "struct" too. The
    /// merged file names no source: every RID agreed.
    /// </summary>
    [Fact]
    public void The_api_file_lays_out_every_struct()
    {
        BuildApiStruct();
        using JsonDocument document = ReadApi(Repo.TestProjectPath("ApiStruct", "bin", "Release", "freestanding", "apistruct.api.json"));
        JsonElement[] structs = document.RootElement.GetProperty("structs").EnumerateArray().ToArray();
        Assert.Equal(ApiStructStructs.Select(s => s.Name), structs.Select(s => s.GetProperty("name").GetString()));
        for (int i = 0; i < structs.Length; i++)
        {
            Assert.Equal(ApiStructStructs[i].Size, structs[i].GetProperty("size").GetInt32());
            Assert.Equal(ApiStructStructs[i].Alignment, structs[i].GetProperty("alignment").GetInt32());
            Assert.Equal(ApiStructStructs[i].Declaration, structs[i].GetProperty("declaration").GetString());
            Assert.Equal(ApiStructStructs[i].Offsets, structs[i].GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("offset").GetInt32()));
            Assert.False(structs[i].TryGetProperty("source", out _), $"{ApiStructStructs[i].Name}: the merged file names no source");
        }

        JsonElement[] hash = structs[0].GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(8, hash[0].GetProperty("count").GetInt32());
        Assert.Equal(64, hash[2].GetProperty("count").GetInt32());
        Assert.False(hash[1].TryGetProperty("count", out _), "length is no array");
        JsonElement rectMin = structs[3].GetProperty("fields")[0].GetProperty("type");
        Assert.Equal("point", rectMin.GetProperty("base").GetString());
        Assert.True(rectMin.GetProperty("struct").GetBoolean());
        Assert.Equal(0, rectMin.GetProperty("pointers").GetInt32());
        JsonElement[] sorter = structs[4].GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(2, sorter[0].GetProperty("type").GetProperty("callback").GetProperty("parameters").GetArrayLength());
        Assert.True(sorter[2].GetProperty("type").GetProperty("struct").GetBoolean());
        Assert.Equal(1, sorter[2].GetProperty("type").GetProperty("pointers").GetInt32());

        JsonElement rectArea = document.RootElement.GetProperty("functions").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "rect_area");
        Assert.Equal("int32_t rect_area(const rect* r)", rectArea.GetProperty("prototype").GetString());
        JsonElement parameter = rectArea.GetProperty("parameters")[0].GetProperty("type");
        Assert.True(parameter.GetProperty("struct").GetBoolean());
        Assert.True(parameter.GetProperty("const").GetBoolean());
    }

    /// <summary>
    /// Each source gets the constants of the structs it declares or includes, as C would see
    /// them, before it is assembled: NASM as preprocessor numbers (%assign) in a pre-include, GNU
    /// as symbols (.set) that the source's wrapper includes. shapes.asm includes only point and
    /// rect, so its file holds no mixed; kinds.asm's holds the include's structs first.
    /// </summary>
    [Fact]
    public void Each_source_gets_the_constants_of_its_structs()
    {
        BuildApiStruct();
        string x64 = Repo.TestProjectPath("ApiStruct", "obj", "Release", "freestanding", "linux-x64", "asm", "structs");
        string kinds = File.ReadAllText(Path.Combine(x64, "kinds.asm.inc"));
        Assert.Contains("%assign point.y 4\n", kinds);
        Assert.Contains("%assign mixed.tag 22\n", kinds);
        Assert.Contains("%assign hash_state_size 112\n", kinds);
        Assert.True(kinds.IndexOf("rect_size", StringComparison.Ordinal) < kinds.IndexOf("hash_state_size", StringComparison.Ordinal), "the include's structs come first");
        string shapes = File.ReadAllText(Path.Combine(x64, "shapes.asm.inc"));
        Assert.Contains("%assign rect.max 8\n", shapes);
        Assert.DoesNotContain("mixed", shapes);

        string arm64 = Repo.TestProjectPath("ApiStruct", "obj", "Release", "freestanding", "linux-arm64", "asm");
        Assert.Contains(".set mixed.tag, 22\n", File.ReadAllText(Path.Combine(arm64, "structs", "kinds.s.inc")));
        string wrapper = File.ReadAllText(Path.Combine(arm64, "implicit", "kinds.s"));
        Assert.Contains(".include \"obj/Release/freestanding/linux-arm64/asm/structs/kinds.s.inc\"\n", wrapper);
    }

    /// <summary>
    /// tests/projects/NetStructs fills each of ApiStruct's structs in C# (generated sequential
    /// structs, nothing written by hand) and has the asm code read them through the SDK's offsets:
    /// nested structs, fixed buffers, a bool, a pointer after padding, a char, a function pointer
    /// field the asm code calls, a struct the asm code writes. The size constants of the asm code
    /// equal C#'s sizeof for every struct. A field at another offset on either side would give a
    /// wrong number on this machine.
    /// </summary>
    [Fact]
    public void A_program_uses_the_structs_through_the_bindings()
    {
        HostRidOrSkip();
        CommandResult build = sdk.Once("build NetStructs", () => sdk.Dotnet("build", Repo.TestProjectPath("NetStructs", "NetStructs.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetStructs", "bin", "Release", "net10.0", "NetStructs.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["rect_area 15", "point_set 40 2", "state_sum 144", "sorter_call 42", "mixed_get 42", "sizes 8=8 20=20 112=112 24=24 24=24"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// The baseline's structs are the spec's: sequential, at the root of the library's namespace,
    /// each field a C# equivalent of the same size (a fixed buffer for an array, bool for bool,
    /// byte for char, a delegate* unmanaged for a function pointer, the generated struct for a
    /// nested one, fully qualified), with the C line, the size and each offset in the summaries; a
    /// PROTO's pointer to a struct takes the struct's pointer. Read from the source the SDK wrote
    /// (the project never mentions the attribute: no generator).
    /// </summary>
    [Fact]
    public void The_generated_structs_are_the_specs()
    {
        CommandResult build = sdk.Once("build NetStructs emitted", () =>
            sdk.Dotnet("build", Repo.TestProjectPath("NetStructs", "NetStructs.csproj"), "-c", "Emitted", "-p:EmitCompilerGeneratedFiles=true"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string source = File.ReadAllText(Repo.TestProjectPath("NetStructs", "obj", "Emitted", "net10.0", "AsmBindings", "Apistruct.g.cs"));
        Assert.Contains("namespace Apistruct\n{", source);
        Assert.Contains("/// <summary><c>STRUCT rect, point min, point max, uint32_t flags</c>: 20 bytes, aligned to 4.</summary>", source);
        Assert.Matches(new Regex(@"\[global::System\.Runtime\.InteropServices\.StructLayout\(global::System\.Runtime\.InteropServices\.LayoutKind\.Sequential\)\]\s+internal unsafe struct Rect\s"), source);
        Assert.Contains("public global::Apistruct.Point Min;", source);
        Assert.Contains("public fixed uint H[8];", source);
        Assert.Contains("public fixed byte Block[64];", source);
        Assert.Contains("public bool Final;", source);
        Assert.Contains("public byte Tag;", source);
        Assert.Contains("public byte* Data;", source);
        Assert.Contains("public delegate* unmanaged<int, int, int> Cmp;", source);
        Assert.Contains("public global::Apistruct.Sorter* Next;", source);
        Assert.Contains("/// <summary><c>char tag</c>, at offset 22.</summary>", source);
        Assert.Contains("internal static unsafe extern int RectArea(global::Apistruct.Rect* r);", source);
    }

    /// <summary>
    /// Each wrong case of tests/projects/StructErrors stops the build with its code and message,
    /// at the line: the scan's errors (ASM0130, ASM0136 in an include) before anything is
    /// assembled, AsmReadApi's after (a conflict between two sources, a struct passed by value),
    /// and the outer build's ASM0133 (two RIDs that lay a struct out differently), which compares
    /// whole RIDs and has no line.
    /// </summary>
    /// <param name="structCase">The case's folder.</param>
    /// <param name="location">The expected location's end ("" for none), as "file(line)".</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A part of the message.</param>
    [Theory]
    [InlineData("badfield", "badfield.asm(3)", "ASM0130", "'long' differs between Windows and the other platforms")]
    [InlineData("pointerarray", "pointerarray.asm(3)", "ASM0130", "rows[4] is an array of pointers, and C#'s fixed buffers hold bool, the fixed-width integers, float and double only")]
    [InlineData("selfvalue", "selfvalue.asm(4)", "ASM0130", "node holds itself by value (inner), which would make it infinitely large: point at it instead (node* inner)")]
    [InlineData("undeclared", "undeclared.asm(4)", "ASM0130", "unknown type 'point'")]
    [InlineData("conflictinclude", "other.inc(3)", "ASM0136", "STRUCT pair declares pair again, differently: \"pair, uint64_t a, uint64_t b\" here, \"pair, uint32_t a, uint32_t b\" in")]
    [InlineData("conflict", "b.asm(3)", "ASM0136", "STRUCT pair declares pair differently from cases/conflict/a.asm")]
    [InlineData("byvalue", "byvalue.asm(5)", "ASM0130", "point is a struct passed by value, which each platform passes its own way")]
    [InlineData("drift", "", "ASM0133", "The RIDs declare struct shape differently: win-x64: shape, uint64_t a (8 bytes, in cases/drift/s.win.asm); linux-x64: shape, uint32_t a (4 bytes, in cases/drift/s.asm)")]
    public void A_wrong_struct_line_stops_the_build_with_its_code(string structCase, string location, string code, string message)
    {
        CommandResult build = BuildStructCase(structCase);
        Assert.True(build.ExitCode != 0, $"{structCase} built: {build.Tail()}");
        Match error = Regex.Match(build.StdOut, $@"(?<location>\S*): error {code}: [^\r\n]*{Regex.Escape(message)}");
        Assert.True(error.Success, $"no {code} with \"{message}\" in: {build.Tail()}");
        if (location.Length > 0)
        {
            Assert.EndsWith(location, error.Groups["location"].Value.Replace('\\', '/'));
        }
    }

    /// <summary>
    /// A STRUCT written by another macro is one the SDK could not read (a macro's body is not
    /// assembled where it is written): the STRUCT macro stops the build when it expands, in NASM
    /// and in GNU syntax, saying why and what to write.
    /// </summary>
    /// <param name="structCase">bymacro (NASM) or gnubymacro (llvm-mc).</param>
    [Theory]
    [InlineData("bymacro")]
    [InlineData("gnubymacro")]
    public void A_struct_written_by_a_macro_stops_the_build(string structCase)
    {
        CommandResult build = BuildStructCase(structCase);
        Assert.True(build.ExitCode != 0, $"{structCase} built: {build.Tail()}");
        Assert.Contains("STRUCT made was not read by the SDK, which reads the STRUCT lines of the source and of its include files before assembling: write it as a line of its own, not from another macro.", build.StdOut);
    }

    /// <summary>
    /// With ImplicitUsings off, sources include the platform layer themselves and still get their
    /// constants (tests/projects/StructRaw): NASM through a pre-include of their own, and a GNU
    /// source through a wrapper written for it only because it has a STRUCT line. Both check the
    /// layout with %if / .if, so the build is the proof; the API file lists the struct. A second
    /// build with nothing changed assembles nothing: the constants files are inputs of the
    /// assemble step, and they are rewritten only when their text changes.
    /// </summary>
    [Fact]
    public void Structs_work_with_implicit_usings_off_and_rebuild_nothing_unchanged()
    {
        CommandResult build = sdk.BuildTestProject("StructRaw");
        Assert.True(build.ExitCode == 0, build.Tail());
        using (JsonDocument document = ReadApi(Repo.TestProjectPath("StructRaw", "bin", "Release", "freestanding", "structraw.api.json")))
        {
            Assert.Equal("pair, uint8_t tag, uint64_t value", document.RootElement.GetProperty("structs")[0].GetProperty("declaration").GetString());
        }

        string wrapper = Repo.TestProjectPath("StructRaw", "obj", "Release", "freestanding", "linux-arm64", "asm", "implicit", "pair.s");
        Assert.True(File.Exists(wrapper), "no wrapper for the GNU source with a STRUCT line");
        string text = File.ReadAllText(wrapper);
        Assert.DoesNotContain("asmsdk-gnu-implicit.inc", text);
        Assert.Contains("structs/pair.s.inc", text);

        CommandResult again = sdk.Dotnet("build", Repo.TestProject("StructRaw"), "-c", "Release", "-v:n");
        Assert.True(again.ExitCode == 0, again.Tail());
        Assert.Empty(IncrementalBuildTests.ToolRun.Matches(again.StdOut).Select(m => m.Value));
    }

    /// <summary>
    /// A STRUCT line in a WebAssembly source stops the build with the reason, at the line, as a
    /// PROTO line does there (tests/projects/WasmStruct): the SDK lays structs out for the native
    /// RIDs only.
    /// </summary>
    [Fact]
    public void A_struct_line_in_a_wasm_source_stops_the_build()
    {
        CommandResult build = sdk.BuildTestProject("WasmStruct");
        Assert.True(build.ExitCode != 0, $"WasmStruct built: {build.Tail()}");
        Assert.Contains("STRUCT is not read from WebAssembly sources yet", build.StdOut, StringComparison.Ordinal);
        Assert.Contains("shape.wasm.s:3:1: note: while in macro instantiation", build.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generator's struct errors (tests/projects/NetStructsErrors), each at its place: a public
    /// class that binds a function taking a struct of the internal default class (ASM0145, at the
    /// attribute, naming the fix), and a struct named like a file's class in the default class
    /// (ASM0143, the class's own error, without a location).
    /// </summary>
    /// <param name="structCase">The case's file under cases/.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A part of the message.</param>
    [Theory]
    [InlineData("publicstruct", "ASM0145", "NetStructsErrors.PublicShapes binds point_set as public, but point_set takes the struct point of apistruct, which the library's baseline keeps internal")]
    [InlineData("clash", "ASM0143", "In the bindings of apistructclash (namespace Apistructclash): clash becomes the class Apistructclash.Clash, the name of a struct")]
    public void A_wrong_struct_binding_stops_the_build_with_its_code(string structCase, string code, string message)
    {
        CommandResult build = sdk.Once($"build NetStructsErrors {structCase}", () =>
            sdk.Dotnet("build", Repo.TestProjectPath("NetStructsErrors", "NetStructsErrors.csproj"), "-c", $"Structs{structCase}", $"-p:StructCase={structCase}"));
        Assert.True(build.ExitCode != 0, $"{structCase} built: {build.Tail()}");
        Assert.Matches(new Regex($@"error {code}: [^\r\n]*{Regex.Escape(message)}"), build.StdOut);
    }

    /// <summary>Builds one case of tests/projects/StructErrors in a configuration of its own (no case sees another's objects).</summary>
    /// <param name="structCase">The case's folder.</param>
    /// <returns>The build's result.</returns>
    private CommandResult BuildStructCase(string structCase) =>
        sdk.Once($"build StructErrors {structCase}", () =>
            sdk.Dotnet("build", Repo.TestProject("StructErrors"), "-c", $"StructCase{structCase}", $"-p:StructCase={structCase}"));

    /// <summary>Builds tests/projects/ApiStruct in Release (all eight RIDs) once, asserting success.</summary>
    /// <exception cref="Xunit.Sdk.XunitException">The build failed; the message holds the end of its log.</exception>
    private void BuildApiStruct()
    {
        CommandResult build = sdk.BuildTestProject("ApiStruct");
        Assert.True(build.ExitCode == 0, build.Tail());
    }

    /// <summary>Reads an API file, asserting it exists.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The parsed document (the caller disposes it).</returns>
    /// <exception cref="Xunit.Sdk.XunitException">The file does not exist.</exception>
    private static JsonDocument ReadApi(string path)
    {
        Assert.True(File.Exists(path), $"no {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
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
