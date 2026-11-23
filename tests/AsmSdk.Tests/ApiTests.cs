using System.Text.Json;
using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// PROTO lines and the API file, the first phase of the generated C# bindings: asmsdk.inc and
/// asmsdk-gnu.inc record every PROTO line (and a record per function macro) in a metadata section
/// of each object; the SDK reads them per RID, checks them, writes the RID's API file, and in the
/// RID outer build checks that every RID agrees and writes the library's API file.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// tests/projects/ApiProto has one function per kind of prototype, for all eight RIDs (x64 in
/// NASM, ARM64 in GNU syntax). tests/projects/ApiErrors has one wrong case per folder, each built
/// in a configuration of its own so that no case sees another's objects.
/// </remarks>
public sealed class ApiTests(SdkUnderTest sdk)
{
    /// <summary>The functions of ApiProto, sorted by name, with their logical files and canonical prototypes.</summary>
    private static readonly (string Name, string File, bool Fast, string Prototype)[] ApiProtoFunctions =
    [
        ("add_ints", "add", false, "int32_t add_ints(int32_t a, int32_t b)"),
        ("answer", "answer", false, "int32_t answer(void)"),
        ("apply", "callback", false, "int32_t apply(int32_t (*op)(int32_t, int32_t), int32_t a, int32_t b)"),
        ("halve", "halve", false, "double halve(double value)"),
        ("name_length", "strings", false, "size_t name_length(const char* name)"),
        ("sum_bytes", "simd/sum", true, "fast uint32_t sum_bytes(const uint8_t* data, size_t len)"),
    ];

    /// <summary>The Windows and macOS RIDs, whose linkers drop the metadata section.</summary>
    public static readonly TheoryData<string> DroppingRids = ["win-x64", "win-arm64", "osx-x64", "osx-arm64"];

    /// <summary>The Linux RIDs, whose shared libraries keep the section unloaded.</summary>
    public static readonly TheoryData<string> LinuxRids = ["linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"];

    /// <summary>
    /// The library's API file (bin/Release/freestanding/apiproto.api.json, written by the RID
    /// outer build) lists every PROTO line once, sorted by name: its logical file (the source's
    /// path without extension and RID qualifier, so answer.win.asm, answer.asm and answer.s are
    /// all "answer"), "fast", the canonical prototype, and the parsed types. The per-RID fields
    /// (the RID, each function's source) are left out: every RID agreed.
    /// </summary>
    [Fact]
    public void The_api_file_describes_every_proto_line()
    {
        BuildApiProto();
        using JsonDocument document = ReadApi(Repo.TestProjectPath("ApiProto", "bin", "Release", "freestanding", "apiproto.api.json"));
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.Equal("apiproto", root.GetProperty("library").GetString());
        Assert.False(root.TryGetProperty("runtimeIdentifier", out _), "the merged file names no RID");
        JsonElement[] functions = root.GetProperty("functions").EnumerateArray().ToArray();
        Assert.Equal(ApiProtoFunctions.Select(f => f.Name), functions.Select(f => f.GetProperty("name").GetString()));
        for (int i = 0; i < functions.Length; i++)
        {
            Assert.Equal(ApiProtoFunctions[i].File, functions[i].GetProperty("file").GetString());
            Assert.Equal(ApiProtoFunctions[i].Fast, functions[i].GetProperty("fast").GetBoolean());
            Assert.Equal(ApiProtoFunctions[i].Prototype, functions[i].GetProperty("prototype").GetString());
            Assert.False(functions[i].TryGetProperty("source", out _), $"{ApiProtoFunctions[i].Name}: the merged file names no source");
        }

        // The parsed types the generator will read: a const byte pointer, a size, a callback.
        JsonElement sum = functions[5].GetProperty("parameters");
        Assert.Equal("data", sum[0].GetProperty("name").GetString());
        Assert.Equal("uint8_t", sum[0].GetProperty("type").GetProperty("base").GetString());
        Assert.True(sum[0].GetProperty("type").GetProperty("const").GetBoolean());
        Assert.Equal(1, sum[0].GetProperty("type").GetProperty("pointers").GetInt32());
        Assert.Equal("size_t", sum[1].GetProperty("type").GetProperty("base").GetString());
        JsonElement callback = functions[2].GetProperty("parameters")[0].GetProperty("type").GetProperty("callback");
        Assert.Equal("int32_t", callback.GetProperty("returns").GetProperty("base").GetString());
        Assert.Equal(2, callback.GetProperty("parameters").GetArrayLength());
    }

    /// <summary>
    /// Each RID writes its own API file beside its library, from its own sources: the per-RID
    /// variants show in each function's source (answer.win.asm on win-x64, answer.asm on the
    /// other x64 RIDs, answer.s on ARM64), and the RID is named.
    /// </summary>
    /// <param name="rid">The runtime identifier.</param>
    [Theory]
    [MemberData(nameof(CrossPlatformTests.AllRids), MemberType = typeof(CrossPlatformTests))]
    public void Each_rid_writes_its_api_file_from_its_own_sources(string rid)
    {
        BuildApiProto();
        using JsonDocument document = ReadApi(Repo.TestProjectPath("ApiProto", "bin", "Release", "freestanding", rid, "apiproto.api.json"));
        Assert.Equal(rid, document.RootElement.GetProperty("runtimeIdentifier").GetString());
        Dictionary<string, string?> sources = document.RootElement.GetProperty("functions").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("source").GetString());
        bool arm64 = rid.EndsWith("arm64", StringComparison.Ordinal);
        Assert.Equal(rid == "win-x64" ? "answer.win.asm" : arm64 ? "answer.s" : "answer.asm", sources["answer"]);
        Assert.Equal(arm64 ? "simd/sum.s" : "simd/sum.asm", sources["sum_bytes"]);
    }

    /// <summary>
    /// The records never reach a Windows or macOS image: lld-link drops COFF's info section, and
    /// ld64.lld drops Mach-O's __DWARF debug section (measured 2026-11-23, now checked).
    /// </summary>
    /// <param name="rid">A Windows or macOS runtime identifier.</param>
    [Theory]
    [MemberData(nameof(DroppingRids))]
    public void The_records_never_reach_a_windows_or_macos_image(string rid)
    {
        BuildApiProto();
        string library = Repo.TestProjectPath("ApiProto", "bin", "Release", "freestanding", rid, CrossPlatformTests.LibraryFile(rid, "apiproto"));
        string sections = sdk.ReadObj(library, "--sections");
        Assert.Contains("Name: ", sections);
        Assert.DoesNotContain("asmapi", sections);
    }

    /// <summary>
    /// A Linux shared library keeps the section, not loaded: ld.lld keeps non-allocated sections
    /// (as it keeps .comment), so the library carries its own API description at no run-time cost.
    /// </summary>
    /// <param name="rid">A Linux runtime identifier.</param>
    [Theory]
    [MemberData(nameof(LinuxRids))]
    public void A_linux_library_carries_the_records_unloaded(string rid)
    {
        BuildApiProto();
        string library = Repo.TestProjectPath("ApiProto", "bin", "Release", "freestanding", rid, CrossPlatformTests.LibraryFile(rid, "apiproto"));
        // llvm-readobj's own style lists each section as a block; the .asmapi block's flags must
        // not hold SHF_ALLOC (GNU style's flag column shifts when it is empty, so it is not read).
        string sections = sdk.ReadObj(library, "--sections");
        int start = sections.IndexOf("Name: .asmapi", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{rid}: no .asmapi section in {sections}");
        int end = sections.IndexOf("Section {", start, StringComparison.Ordinal);
        string block = end < 0 ? sections[start..] : sections[start..end];
        Assert.Contains("SHT_PROGBITS", block);
        Assert.DoesNotContain("SHF_ALLOC", block);
    }

    /// <summary>
    /// A project whose sources have function macros but no PROTO line writes no API file, in the
    /// outer build or in any RID's folder: no API, no bindings (tests/projects/Everywhere).
    /// </summary>
    [Fact]
    public void A_project_without_proto_lines_writes_no_api_file()
    {
        CommandResult build = sdk.BuildTestProject("Everywhere");
        Assert.True(build.ExitCode == 0, build.Tail());
        string output = Repo.TestProjectPath("Everywhere", "bin", "Release", "freestanding");
        Assert.Empty(Directory.GetFiles(output, "*.api.json", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Each wrong case of tests/projects/ApiErrors stops the build with its code and message, at
    /// the PROTO line (found by searching the source, since the records carry no line numbers;
    /// ASM0133 is reported by the outer build, which compares whole RIDs, without a location).
    /// </summary>
    /// <param name="apiCase">The case's folder.</param>
    /// <param name="location">The expected location prefix ("" for none), as "file(line)".</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A part of the message.</param>
    [Theory]
    [InlineData("badtype", "badtype.asm(4)", "ASM0130", "'long' differs between Windows and the other platforms")]
    [InlineData("nofunction", "nofunction.asm(4)", "ASM0131", "no FUNCTION of this RID's sources is named ghost")]
    [InlineData("nofunction", "nofunction.asm(6)", "ASM0131", "helper is a LOCAL_FUNCTION, callable from its file only")]
    [InlineData("hidden", "hidden.asm(4)", "ASM0131", "inner_answer is a HIDDEN_FUNCTION, which no image exports")]
    [InlineData("count", "count.asm(4)", "ASM0132", "PROTO add3 has 3 integer or pointer argument(s), but its FUNCTION in cases/count/count.asm says 2")]
    [InlineData("drift", "", "ASM0133", "win-x64: int32_t add_ints(int32_t a, int64_t b) (in cases/drift/add.win.asm); linux-x64: int32_t add_ints(int32_t a, int32_t b) (in cases/drift/add.asm)")]
    [InlineData("duplicate", "duplicate.asm(4)", "ASM0134", "A second PROTO for twice")]
    public void A_wrong_proto_line_stops_the_build_with_its_code(string apiCase, string location, string code, string message)
    {
        // A configuration per case (-c, the build's only Configuration): no case sees another's
        // objects, and the two nofunction rows share one build.
        CommandResult build = sdk.Once($"build ApiErrors {apiCase}", () =>
            sdk.Dotnet("build", Repo.TestProject("ApiErrors"), "-c", $"ApiCase{apiCase}", $"-p:ApiCase={apiCase}"));
        Assert.True(build.ExitCode != 0, $"{apiCase} built: {build.Tail()}");
        Match error = Regex.Match(build.StdOut, $@"(?<location>\S*): error {code}: [^\r\n]*{Regex.Escape(message)}");
        Assert.True(error.Success, $"no {code} with \"{message}\" in: {build.Tail()}");
        if (location.Length > 0)
        {
            Assert.EndsWith(location, error.Groups["location"].Value.Replace('\\', '/'));
        }
    }

    /// <summary>
    /// A PROTO for a HIDDEN_FUNCTION is bindable from a static library: its users link the
    /// function (a NativeAOT app among them), so the library's API file is written.
    /// </summary>
    [Fact]
    public void A_hidden_function_is_bindable_from_a_static_library()
    {
        CommandResult build = sdk.Dotnet("build", Repo.TestProject("ApiErrors"), "-c", "ApiCaseHiddenStatic", "-p:ApiCase=hidden", "-p:OutputType=StaticLibrary");
        Assert.True(build.ExitCode == 0, build.Tail());
        using JsonDocument document = ReadApi(Repo.TestProjectPath("ApiErrors", "bin", "ApiCaseHiddenStatic", "freestanding", "apierrors.api.json"));
        Assert.Equal("int32_t inner_answer(void)", document.RootElement.GetProperty("functions")[0].GetProperty("prototype").GetString());
    }

    /// <summary>Builds tests/projects/ApiProto in Release (all eight RIDs) once, asserting success.</summary>
    /// <exception cref="Xunit.Sdk.XunitException">The build failed; the message holds the end of its log.</exception>
    private void BuildApiProto()
    {
        CommandResult build = sdk.BuildTestProject("ApiProto");
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
}
