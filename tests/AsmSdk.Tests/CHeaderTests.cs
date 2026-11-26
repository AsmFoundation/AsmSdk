using System.IO.Compression;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The C header the SDK writes from a library's PROTO and STRUCT lines (the owner's choice of
/// 2026-11-26: "Build it for 0.4.0", on by default, AsmGenerateCHeader=false to opt out):
/// &lt;AssemblyName&gt;.h beside each API file, packed under include/, with the functions'
/// declarations, the structs and checks of their layout; a project's own include/&lt;name&gt;.h
/// wins (ASM0149).
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// The strongest check needs a C compiler, which the Linux and macOS machines have (cc): a C
/// program compiled against the header calls the real library and reads back every kind of struct
/// field. Elsewhere that test skips with the reason; the others run everywhere.
/// </remarks>
public sealed class CHeaderTests(SdkUnderTest sdk)
{
    /// <summary>
    /// tests/projects/ApiStruct gets apistruct.h beside its merged API file and beside each RID's,
    /// the same text in both (every RID agrees on the API): the include guard, extern "C", the
    /// structs in an order C can define them (point before rect, which holds it by value), every
    /// field kind (an array, a bool, a function pointer, a pointer to the struct itself), the
    /// layout checks with the SDK's numbers, and the declarations rendered from the model (no
    /// "fast" keyword, which PROTO lines keep).
    /// </summary>
    [Fact]
    public void A_library_with_proto_and_struct_lines_gets_a_c_header()
    {
        CommandResult build = sdk.Once("build ApiStruct", () => sdk.Dotnet("build", Repo.TestProject("ApiStruct"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string header = File.ReadAllText(Repo.TestProjectPath("ApiStruct", "bin", "Release", "freestanding", "apistruct.h"));
        Assert.Equal(header, File.ReadAllText(Repo.TestProjectPath("ApiStruct", "bin", "Release", "freestanding", "linux-x64", "apistruct.h")));

        Assert.Contains("#ifndef APISTRUCT_H", header);
        Assert.Contains("extern \"C\" {", header);
        Assert.Contains("#include <stdbool.h>", header);
        Assert.Contains("typedef struct sorter sorter;", header);
        Assert.True(header.IndexOf("struct point {", StringComparison.Ordinal) < header.IndexOf("struct rect {", StringComparison.Ordinal), "point must be defined before rect, which holds it");
        Assert.Contains("    point max; /* offset 8 */", header);
        Assert.Contains("    uint32_t h[8]; /* offset 0 */", header);
        Assert.Contains("    bool final; /* offset 104 */", header);
        Assert.Contains("    int32_t (*cmp)(int32_t, int32_t); /* offset 0 */", header);
        Assert.Contains("    sorter* next; /* offset 16 */", header);
        Assert.Contains("APISTRUCT_LAYOUT(sizeof(hash_state) == 112, \"hash_state: 112 bytes in the asm code\");", header);
        Assert.Contains("APISTRUCT_LAYOUT(offsetof(rect, max) == 8, \"rect.max: offset 8 in the asm code\");", header);
        Assert.Contains("int32_t rect_area(const rect* r);", header);
        Assert.Contains("void point_set(point* p, int32_t x, int32_t y);", header);
        Assert.Contains("size_t struct_sizes(size_t* out);", header);
    }

    /// <summary>
    /// A C program (tests/projects/CHeaderCaller/main.c) compiled against apistruct.h, as C11 and
    /// as C99, with warnings as errors, links the host RID's libapistruct and runs: every kind of
    /// struct field it fills is read back right by the asm code, and the asm code's size constants
    /// equal C's sizeof. The header also compiles as C++11. Where no C compiler is on PATH (the
    /// Windows machines) the test skips; the Linux and macOS machines run it.
    /// </summary>
    [Fact]
    public void A_c_program_calls_the_library_through_its_header()
    {
        string? rid = CrossPlatformTests.HostBuildRid();
        if (rid is null || rid.StartsWith("win", StringComparison.Ordinal) || !Command.OnPath("cc"))
        {
            Assert.Skip("No C compiler (cc) here, or a Windows machine: the Linux and macOS machines run this");
        }

        CommandResult build = sdk.Once("build ApiStruct", () => sdk.Dotnet("build", Repo.TestProject("ApiStruct"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string include = Repo.TestProjectPath("ApiStruct", "bin", "Release", "freestanding");
        string library = Path.Combine(include, rid);
        string output = Path.Combine(Repo.Artifacts, "test-run", "c-header");
        Directory.CreateDirectory(output);
        var environment = new Dictionary<string, string?>
        {
            ["LD_LIBRARY_PATH"] = library,
            ["DYLD_LIBRARY_PATH"] = library,
        };
        foreach (string standard in new[] { "c11", "c99" })
        {
            string program = Path.Combine(output, "caller_" + standard);
            CommandResult compile = Command.Run("cc", [$"-std={standard}", "-Wall", "-Wextra", "-pedantic", "-Werror", $"-I{include}",
                Repo.TestProjectPath("CHeaderCaller", "main.c"), $"-L{library}", "-lapistruct", $"-Wl,-rpath,{library}", "-o", program], Repo.Root);
            Assert.True(compile.ExitCode == 0, $"{standard}: {compile.Output}");
            CommandResult run = Command.Run(program, [], Repo.Root, environment);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal(
                ["rect_area 15", "point_set 40 2", "state_sum 144", "sorter_call 42", "mixed_get 42", "sizes 8=8 20=20 112=112 24=24 24=24"],
                run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        string cpp = Path.Combine(output, "header.cpp");
        File.WriteAllText(cpp, "#include \"apistruct.h\"\nint main() { return 0; }\n");
        CommandResult cplusplus = Command.Run("c++", ["-std=c++11", "-Wall", "-Wextra", "-pedantic", "-Werror", $"-I{include}", "-fsyntax-only", cpp], Repo.Root);
        Assert.True(cplusplus.ExitCode == 0, cplusplus.Output);
    }

    /// <summary>AsmGenerateCHeader=false: no header beside the API files (ApiProto, in a configuration of its own).</summary>
    [Fact]
    public void AsmGenerateCHeader_false_writes_no_header()
    {
        CommandResult build = sdk.Dotnet("build", Repo.TestProject("ApiProto"), "-c", "NoCHeader", "-p:AsmGenerateCHeader=false");
        Assert.True(build.ExitCode == 0, build.Tail());
        string folder = Repo.TestProjectPath("ApiProto", "bin", "NoCHeader", "freestanding");
        Assert.True(File.Exists(Path.Combine(folder, "apiproto.api.json")), "no API file: the test would pass for the wrong reason");
        Assert.Empty(Directory.GetFiles(folder, "*.h", SearchOption.AllDirectories));
    }

    /// <summary>
    /// tests/projects/CHeaderOwn ships its own include/cheaderown.h: the build writes no header of
    /// its own, and dotnet pack warns ASM0149 and carries the project's file under include/.
    /// </summary>
    [Fact]
    public void A_projects_own_header_wins()
    {
        CommandResult pack = sdk.Dotnet("pack", Repo.TestProject("CHeaderOwn"), "-c", "Release");
        Assert.True(pack.ExitCode == 0, pack.Tail());
        Assert.Contains("warning ASM0149: include/cheaderown.h is the project's own", pack.StdOut);
        Assert.Empty(Directory.GetFiles(Repo.TestProjectPath("CHeaderOwn", "bin", "Release", "freestanding"), "*.h", SearchOption.AllDirectories));
        using ZipArchive archive = ZipFile.OpenRead(Path.Combine(Repo.Artifacts, "packages", "Test.CHeaderOwn.1.0.0.nupkg"));
        ZipArchiveEntry? entry = archive.GetEntry("include/cheaderown.h");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.Contains("written by hand", reader.ReadToEnd());
    }
}
