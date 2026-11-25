using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The generated C# bindings, end to end (phases 2 and 4 of catalog asmsdk/plans/csharp-bindings.md):
/// a C# project that references an asm library with PROTO lines gets its baseline (the namespace
/// named after the library: blittable [DllImport] declarations and the wrappers that convert bools
/// and strings), written by the SDK while the project never mentions [AsmLibrary], and by the
/// generator, with the copies the attributes ask for, once it does; it calls the library with
/// nothing written by hand.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
/// <remarks>
/// The programs run on this machine (its RID's library, from the portable build's runtimes/
/// folder), so each CI machine proves its own platform's calling convention: Windows x64 passes
/// arguments in rcx/rdx/r8, System V in rdi/rsi/rdx, ARM64 in x0-x2.
/// </remarks>
public sealed class BindingsTests(SdkUnderTest sdk)
{
    /// <summary>
    /// tests/projects/NetBindings calls all six ApiProto functions through the library's baseline
    /// (the namespace Apiproto, a class per file): two integers, a function named like its file
    /// (Invoke), a double, a UTF-8 string, a span (the pointer and length pair), and a callback.
    /// Every line must come out right on this machine. The project never mentions the attribute,
    /// so no generator ran: the attribute's type is not in the assembly (the switch).
    /// </summary>
    [Fact]
    public void A_program_calls_the_asm_library_through_its_baseline()
    {
        HostRidOrSkip();
        CommandResult build = sdk.Once("build NetBindings", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindings", "NetBindings.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetBindings", "bin", "Release", "net10.0", "NetBindings.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["add_ints 42", "answer 42", "halve 2.5", "name_length 5", "sum_bytes 10", "apply 42", "attribute False"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// The baseline the SDK writes (obj/.../AsmBindings/Apiproto.g.cs, no generator) is the spec's:
    /// the namespace named after the library, folders as namespaces, a class per file, internal;
    /// [SuppressGCTransition] on the fast function; UTF-8 strings; a function pointer as delegate*
    /// unmanaged; the span overload pinning its argument; the C prototype as each method's
    /// summary. No generator output exists, in a configuration of its own built with
    /// EmitCompilerGeneratedFiles: the attribute's source would be there if one had run.
    /// </summary>
    [Fact]
    public void The_written_baseline_is_the_specs()
    {
        string generated = Repo.TestProjectPath("NetBindings", "obj", "Emitted", "net10.0", "generated");
        CommandResult build = sdk.Once("build NetBindings emitted", () =>
        {
            // The compiler writes the files of the generators that ran and never deletes those of
            // an earlier build, so a leftover folder would fail the check below for nothing.
            if (Directory.Exists(generated))
            {
                Directory.Delete(generated, recursive: true);
            }

            return sdk.Dotnet("build", Repo.TestProjectPath("NetBindings", "NetBindings.csproj"), "-c", "Emitted", "-p:EmitCompilerGeneratedFiles=true");
        });
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.False(Directory.Exists(Path.Combine(generated, "AsmFoundation.Sdk.Bindings")), "a bindings generator ran");
        string source = File.ReadAllText(Repo.TestProjectPath("NetBindings", "obj", "Emitted", "net10.0", "AsmBindings", "Apiproto.g.cs"));
        Assert.Contains("namespace Apiproto\n{", source);
        Assert.Contains("namespace Apiproto.Simd\n{", source);
        Assert.Contains("internal static partial class Sum", source);
        Assert.Contains("internal static partial class Add", source);
        Assert.DoesNotContain("AsmLibrary", source);
        Assert.Matches(new Regex(@"\[global::System\.Runtime\.InteropServices\.DllImport\(""apiproto"", EntryPoint = ""sum_bytes"", ExactSpelling = true\)\]\s+\[global::System\.Runtime\.InteropServices\.SuppressGCTransition\]\s+internal static unsafe extern uint SumBytes\(byte\* data, nuint len\);"), source);
        Assert.Contains("internal static unsafe uint SumBytes(global::System.ReadOnlySpan<byte> data)", source);
        Assert.Contains("fixed (byte* __p0 = data)", source);
        // The string's conversion, as LibraryImport would have generated it: a stack buffer of
        // Utf8StringMarshaller's size, freed after the call, around a blittable private extern.
        Assert.Contains("private static unsafe extern nuint __NameLength_Native(byte* name);", source);
        Assert.Contains("internal static unsafe nuint NameLength(string name)", source);
        Assert.Contains("__s0.FromManaged(name, stackalloc byte[global::System.Runtime.InteropServices.Marshalling.Utf8StringMarshaller.ManagedToUnmanagedIn.BufferSize]);", source);
        Assert.Contains("__s0.Free();", source);
        Assert.Contains("internal static unsafe extern int Apply(delegate* unmanaged<int, int, int> op, int a, int b);", source);
        Assert.Contains("internal static extern int Invoke();", source);
        Assert.Contains("/// <summary><c>fast uint32_t sum_bytes(const uint8_t* data, size_t len)</c></summary>", source);
    }

    /// <summary>
    /// tests/projects/NetBindingsOptions: [AsmLibrary] at every level, so the generator runs. The
    /// assembly reshapes ApiProto's baseline with Default* options (the namespace Proto, a class per
    /// folder, instantiable, virtual, protected internal: FixedApi overrides it) and turns the
    /// copies' span overloads off. The copies: Text (public), Vector (spans back on), NoSpans and
    /// MathB (AliasBinding between the app's classes), Calculator (a non-static class: virtual
    /// instance copies, which FixedCalculator overrides). The baseline keeps every function; the
    /// attribute's type is in the assembly.
    /// </summary>
    [Fact]
    public void The_bindings_follow_the_attributes_at_every_level()
    {
        HostRidOrSkip();
        CommandResult build = sdk.Once("build NetBindingsOptions", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsOptions", "NetBindingsOptions.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetBindingsOptions", "bin", "Release", "net10.0", "NetBindingsOptions.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["Proto.Api.AddInts 42", "Proto.Api virtual True protected internal True public class True", "Proto.Api overridden 99",
             "Proto.Simd.SumBytes 10", "Text.NameLength 3 public True", "Vector.SumBytes 10 overloads 2", "NoSpans overloads 1",
             "MathA.AddInts 42 MathB.AddInts 2", "Calculator 42 overridden 7", "attribute True"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // The generator wrote the baseline, so the SDK's written one is gone.
        Assert.Empty(Directory.Exists(Repo.TestProjectPath("NetBindingsOptions", "obj", "Release", "net10.0", "AsmBindings"))
            ? Directory.GetFiles(Repo.TestProjectPath("NetBindingsOptions", "obj", "Release", "net10.0", "AsmBindings"), "*.g.cs")
            : []);
    }

    /// <summary>
    /// tests/projects/NetBindingsShapes shapes ApiProto's baseline with the project file alone (no
    /// attribute, so the SDK writes it): AsmBindings* properties for instance, public, sealed
    /// classes, and the library's AsmBindings item for the namespace Shapes.Native, laid out Flat
    /// (everything in Shapes.Native.Api). The program calls an instance method and prints the
    /// class's shape.
    /// </summary>
    [Fact]
    public void The_project_file_shapes_the_baseline()
    {
        HostRidOrSkip();
        CommandResult build = sdk.Once("build NetBindingsShapes", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsShapes", "NetBindingsShapes.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetBindingsShapes", "bin", "Release", "net10.0", "NetBindingsShapes.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["Api.AddInts 42 Api.Answer 42", "public True sealed True static False instance method True virtual False"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.True(File.Exists(Repo.TestProjectPath("NetBindingsShapes", "obj", "Release", "net10.0", "AsmBindings", "Shapes.Native.g.cs")), "the SDK wrote no baseline");
    }

    /// <summary>
    /// Each wrong attribute of tests/projects/NetBindingsErrors stops the build with the
    /// generator's code, located in the C# file that holds it.
    /// </summary>
    /// <param name="bindingsCase">The case's file under cases/.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A part of the message.</param>
    [Theory]
    [InlineData("unknownlib", "ASM0140", "[AsmLibrary(\"nolib\")] names no asm library this project references with PROTO or STRUCT lines; it references apiproto")]
    [InlineData("unknownfile", "ASM0141", "nofile.asm is not a file or folder of apiproto that has PROTO lines; its files are add, answer, callback, halve, simd/sum, strings")]
    [InlineData("twice", "ASM0142", "add_ints is already bound by NetBindingsErrors.First: the app's classes bind each function once")]
    [InlineData("notpartial", "ASM0143", "the class must be partial")]
    [InlineData("badshape", "ASM0146", "apiproto's baseline: Virtual needs instance members")]
    [InlineData("defaultsonclass", "ASM0146", "the Default* options shape a library's baseline; write them on [assembly: AsmLibrary(...)]")]
    public void A_wrong_attribute_stops_the_build_with_its_code(string bindingsCase, string code, string message)
    {
        CommandResult build = BuildBindingsCase(bindingsCase);
        Assert.True(build.ExitCode != 0, $"{bindingsCase} built: {build.Tail()}");
        Assert.Matches(new Regex($@"cases[/\\]{bindingsCase}\.cs\(\d+,\d+\): error {code}: [^\r\n]*{Regex.Escape(message)}"), build.StdOut);
    }

    /// <summary>
    /// A wrong project setting stops the build where no attribute is used, so where the SDK's
    /// task writes the baseline (tests/projects/NetBindingsErrors' project-file cases): a property
    /// value that is no option's (ASM0146) and an AsmBindings item for no referenced library
    /// (ASM0140, as for an attribute). Before compiling: no C# location.
    /// </summary>
    /// <param name="bindingsCase">The case.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A part of the message.</param>
    [Theory]
    [InlineData("badproperty", "ASM0146", "AsmBindingsVisibility is 'Publik', which is not one of Internal, Public, ProtectedInternal")]
    [InlineData("unknownitem", "ASM0140", "<AsmBindings Include=\"nolib\" /> names no asm library this project references with PROTO or STRUCT lines; it references apiproto")]
    public void A_wrong_project_setting_stops_the_build_with_its_code(string bindingsCase, string code, string message)
    {
        CommandResult build = BuildBindingsCase(bindingsCase);
        Assert.True(build.ExitCode != 0, $"{bindingsCase} built: {build.Tail()}");
        Assert.Matches(new Regex($@"error {code}: [^\r\n]*{Regex.Escape(message)}"), build.StdOut);
    }

    /// <summary>
    /// AsmGenerateBindings=false turns the bindings off: NetBindings then fails to compile at its
    /// first call into the namespace Apiproto, which only the bindings create (CS0103), the proof
    /// that nothing was written.
    /// </summary>
    [Fact]
    public void AsmGenerateBindings_false_turns_the_bindings_off()
    {
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("NetBindings", "NetBindings.csproj"), "-c", "NoBindings", "-p:AsmGenerateBindings=false");
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("error CS0103: The name 'Apiproto' does not exist in the current context", build.StdOut);
    }

    /// <summary>Builds one case of tests/projects/NetBindingsErrors in a configuration of its own, once.</summary>
    /// <param name="bindingsCase">The case.</param>
    /// <returns>The build's result.</returns>
    private CommandResult BuildBindingsCase(string bindingsCase) =>
        sdk.Once($"build NetBindingsErrors {bindingsCase}", () =>
            sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsErrors", "NetBindingsErrors.csproj"), "-c", $"Bindings{bindingsCase}", $"-p:BindingsCase={bindingsCase}"));

    /// <summary>
    /// A program is started, never loaded, so a .NET project that references one gets no
    /// bindings for it, although the program has PROTO lines and an API file (which describes
    /// what a plugin host exports to its plugins). tests/projects/NetApiProgram references
    /// ApiProgram, a linux-x64 program (every machine builds it, and the decision is the build's,
    /// so this runs everywhere), and prints what its own assembly holds: neither the class
    /// Apiprogram nor, the program being its only asm reference, the generator's attribute.
    /// Negative control (measured 2026-11-24): with programs not excluded, the same build wrote
    /// the class, DllImport("apiprogram") and all, and printed "program class True".
    /// </summary>
    [Fact]
    public void A_program_reference_gets_no_bindings()
    {
        CommandResult build = sdk.Once("build NetApiProgram", () => sdk.Dotnet("build", Repo.TestProjectPath("NetApiProgram", "NetApiProgram.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        // The API file the bindings would have been made from exists: without it the test would
        // pass for the wrong reason.
        Assert.True(File.Exists(Repo.TestProjectPath("ApiProgram", "bin", "Release", "libc", "apiprogram.api.json")), "ApiProgram wrote no API file");
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetApiProgram", "bin", "Release", "net10.0", "NetApiProgram.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["program class False", "generator False"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
