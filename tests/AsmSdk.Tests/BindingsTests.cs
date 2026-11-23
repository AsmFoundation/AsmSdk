using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The generated C# bindings, end to end (phase 2 of catalog asmsdk/plans/csharp-bindings.md): a
/// C# project that references an asm library with PROTO lines gets blittable [DllImport]
/// declarations (and the wrappers that convert bools and strings) from the SDK's generator, laid
/// out by its [AsmLibrary] attributes, and calls the library with nothing written by hand.
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
    /// tests/projects/NetBindings calls all six ApiProto functions through the default class
    /// (Apiproto, nested by file): two integers, a function named like its file (Invoke), a double,
    /// a UTF-8 string, a span (the pointer and length pair), and a callback. Every line must come
    /// out right on this machine.
    /// </summary>
    [Fact]
    public void A_program_calls_the_asm_library_through_the_generated_bindings()
    {
        HostRidOrSkip();
        CommandResult build = sdk.Once("build NetBindings", () => sdk.Dotnet("build", Repo.TestProjectPath("NetBindings", "NetBindings.csproj"), "-c", "Release"));
        Assert.True(build.ExitCode == 0, build.Tail());
        CommandResult run = Command.Run("dotnet", [Repo.TestProjectPath("NetBindings", "bin", "Release", "net10.0", "NetBindings.dll")], Repo.Root);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["add_ints 42", "answer 42", "halve 2.5", "name_length 5", "sum_bytes 10", "apply 42"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// The generated source is the spec's: the default class in the project's namespace, internal,
    /// nested by file; [SuppressGCTransition] on the fast function; UTF-8 strings; a function
    /// pointer as delegate* unmanaged; the span overload pinning its argument; the C prototype as
    /// each method's summary. Read from EmitCompilerGeneratedFiles, in a configuration of its own.
    /// </summary>
    [Fact]
    public void The_generated_code_is_the_specs()
    {
        CommandResult build = sdk.Once("build NetBindings emitted", () =>
            sdk.Dotnet("build", Repo.TestProjectPath("NetBindings", "NetBindings.csproj"), "-c", "Emitted", "-p:EmitCompilerGeneratedFiles=true"));
        Assert.True(build.ExitCode == 0, build.Tail());
        string generated = Repo.TestProjectPath("NetBindings", "obj", "Emitted", "net10.0", "generated", "AsmFoundation.Sdk.Bindings",
            "AsmFoundation.Sdk.Bindings.BindingsGenerator");
        string source = File.ReadAllText(Path.Combine(generated, "AsmBindings.NetBindings.Apiproto.g.cs"));
        Assert.Contains("namespace NetBindings", source);
        Assert.Contains("internal static partial class Apiproto", source);
        Assert.Contains("internal static partial class Simd", source);
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
        Assert.True(File.Exists(Path.Combine(generated, "AsmFoundation.Interop.AsmLibraryAttribute.g.cs")), "no attribute source");
    }

    /// <summary>
    /// tests/projects/NetBindingsOptions: [AsmLibrary] at every level. The assembly turns span
    /// overloads off and renames ApiProto's default class Proto, laid out by folder; Text takes
    /// strings.asm, public; Vector takes simd/ with spans back on; Alias and NoSpans copy functions
    /// (AliasBinding), the default class keeping them; NoSpans inherits "no span overloads".
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
            ["Proto.AddInts 42", "Proto.Answer 42", "Alias.AddInts 2", "Text.NameLength 3", "Vector.SumBytes 10",
             "Text public True", "Vector overloads 2", "NoSpans overloads 1"],
            run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// Each wrong attribute of tests/projects/NetBindingsErrors stops the build with the
    /// generator's code, located in the C# file that holds it.
    /// </summary>
    /// <param name="bindingsCase">The case's file under cases/.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">A part of the message.</param>
    [Theory]
    [InlineData("unknownlib", "ASM0140", "[AsmLibrary(\"nolib\")] names no asm library this project references with PROTO lines; it references apiproto")]
    [InlineData("unknownfile", "ASM0141", "nofile.asm is not a file or folder of apiproto that has PROTO lines; its files are add, answer, callback, halve, simd/sum, strings")]
    [InlineData("twice", "ASM0142", "add_ints is already bound by NetBindingsErrors.First")]
    [InlineData("notpartial", "ASM0143", "the class must be static and partial")]
    public void A_wrong_attribute_stops_the_build_with_its_code(string bindingsCase, string code, string message)
    {
        CommandResult build = sdk.Once($"build NetBindingsErrors {bindingsCase}", () =>
            sdk.Dotnet("build", Repo.TestProjectPath("NetBindingsErrors", "NetBindingsErrors.csproj"), "-c", $"Bindings{bindingsCase}", $"-p:BindingsCase={bindingsCase}"));
        Assert.True(build.ExitCode != 0, $"{bindingsCase} built: {build.Tail()}");
        Assert.Matches(new Regex($@"cases[/\\]{bindingsCase}\.cs\(\d+,\d+\): error {code}: [^\r\n]*{Regex.Escape(message)}"), build.StdOut);
    }

    /// <summary>
    /// AsmGenerateBindings=false turns the bindings off: NetBindings then fails to compile at its
    /// "using NetBindings;" (CS0246), a namespace that only the generated class creates, the proof
    /// that nothing was generated (the compiler reports that and stops, measured).
    /// </summary>
    [Fact]
    public void AsmGenerateBindings_false_turns_the_bindings_off()
    {
        CommandResult build = sdk.Dotnet("build", Repo.TestProjectPath("NetBindings", "NetBindings.csproj"), "-c", "NoBindings", "-p:AsmGenerateBindings=false");
        Assert.True(build.ExitCode != 0, build.Tail());
        Assert.Contains("error CS0246: The type or namespace name 'NetBindings' could not be found", build.StdOut);
    }

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
