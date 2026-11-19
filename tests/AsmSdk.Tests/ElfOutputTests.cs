using System.Text.RegularExpressions;
using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The ELF properties of the samples' outputs, read with the bundled llvm-readobj: file types,
/// SONAMEs, DT_NEEDED, interpreters, exports and imports, archive members, debug sections, the
/// symbols NASM received, and what a .NET project received from its references.
/// Outputs go to bin/&lt;Configuration&gt;/&lt;TargetFramework&gt;/, as in .NET.
/// </summary>
/// <param name="sdk">The SDK under test (assembly fixture).</param>
public sealed class ElfOutputTests(SdkUnderTest sdk)
{
    /// <summary>Hello.Library's freestanding build.</summary>
    private static readonly string Library = Repo.Sample("Hello.Library", "bin", "Release", "freestanding", "linux-x64", "libhello.so");

    /// <summary>Hello.Library's libc build (the project is multi-targeted).</summary>
    private static readonly string LibraryLibc = Repo.Sample("Hello.Library", "bin", "Release", "libc", "linux-x64", "libhello.so");

    /// <summary>Hello.LibcLibrary: a libc-only shared library.</summary>
    private static readonly string LibcLibrary = Repo.Sample("Hello.LibcLibrary", "bin", "Release", "libc", "linux-x64", "libhellolibc.so");

    /// <summary>Hello.App: a freestanding (static) executable.</summary>
    private static readonly string App = Repo.Sample("Hello.App", "bin", "Release", "freestanding", "linux-x64", "hello-app");

    /// <summary>Hello.Libc: a libc executable (a PIE).</summary>
    private static readonly string LibcApp = Repo.Sample("Hello.Libc", "bin", "Release", "libc", "linux-x64", "hello-libc");

    /// <summary>Hello.Static: a static library.</summary>
    private static readonly string Archive = Repo.Sample("Hello.Static", "bin", "Release", "freestanding", "linux-x64", "libhellostatic.a");

    /// <summary>Hello.Net's output folder: the .NET program and the .so files it received.</summary>
    private static readonly string NetOutput = Repo.Sample("Hello.Net", "bin", "Release", "net10.0");

    /// <summary>Fails the calling test at once, with the build log, when the samples did not build.</summary>
    private void RequireSamples() =>
        Assert.True(sdk.SamplesBuild.ExitCode == 0, "the samples did not build: " + sdk.SamplesBuild.Tail());

    /// <summary>A shared library is a shared object whose SONAME is its file name.</summary>
    [Fact]
    public void A_shared_library_is_a_shared_object_named_by_its_soname()
    {
        RequireSamples();
        string text = sdk.ReadElf(Library, "-h", "-d");
        Assert.Contains("DYN (Shared object file)", text);
        Assert.Contains("Library soname: [libhello.so]", text);
    }

    /// <summary>A freestanding shared library needs no other library.</summary>
    [Fact]
    public void A_freestanding_shared_library_needs_nothing()
    {
        RequireSamples();
        Assert.DoesNotContain("(NEEDED)", sdk.ReadElf(Library, "-d"));
    }

    /// <summary>A shared library exports its global functions as typed (FUNC) symbols.</summary>
    /// <param name="name">An exported function.</param>
    [Theory]
    [InlineData("hello_add")]
    [InlineData("hello_version")]
    [InlineData("hello_uses_libc")]
    public void A_shared_library_exports_its_global_functions(string name)
    {
        RequireSamples();
        string text = sdk.ReadElf(Library, "--dyn-syms");
        Assert.Matches(new Regex($@"FUNC\s+GLOBAL\s+DEFAULT\s+\d+\s+{name}\b"), text);
    }

    /// <summary>The libc build of the same multi-targeted project needs libc.so.6.</summary>
    [Fact]
    public void The_libc_build_of_a_multi_targeted_library_needs_libc()
    {
        RequireSamples();
        Assert.Contains("Shared library: [libc.so.6]", sdk.ReadElf(LibraryLibc, "-d"));
    }

    /// <summary>
    /// NASM receives the configuration, target framework and platform symbols: -DRELEASE
    /// -DFREESTANDING -DLINUX -DX64 for the freestanding linux-x64 build of Hello.Library,
    /// -DRELEASE -DLIBC -DLINUX -DX64 for its libc build.
    /// </summary>
    [Fact]
    public void NASM_receives_the_configuration_and_runtime_symbols()
    {
        RequireSamples();
        Assert.Matches(new Regex(@"-DRELEASE -DFREESTANDING -DLINUX -DX64 -o ""[^""]*Hello\.Library/obj/Release/freestanding/linux-x64/"), sdk.SamplesBuild.StdOut);
        Assert.Matches(new Regex(@"-DRELEASE -DLIBC -DLINUX -DX64 -o ""[^""]*Hello\.Library/obj/Release/libc/linux-x64/"), sdk.SamplesBuild.StdOut);
    }

    /// <summary>A libc library is linked against the stub (DT_NEEDED libc.so.6) and imports strlen from glibc.</summary>
    [Fact]
    public void A_libc_library_needs_glibc_and_imports_from_it()
    {
        RequireSamples();
        string text = sdk.ReadElf(LibcLibrary, "-d", "--dyn-syms");
        Assert.Contains("Shared library: [libc.so.6]", text);
        Assert.Matches(new Regex(@"UND\s+strlen\b"), text);
    }

    /// <summary>A freestanding executable is static (no interpreter), and its Release build has no DWARF.</summary>
    [Fact]
    public void A_freestanding_executable_is_static_without_DWARF_in_Release()
    {
        RequireSamples();
        string text = sdk.ReadElf(App, "-h", "-l", "-S");
        Assert.Contains("EXEC (Executable file)", text);
        Assert.DoesNotContain("INTERP", text);
        Assert.DoesNotContain(".debug_info", text);
    }

    /// <summary>
    /// A libc executable is a PIE with glibc's loader. llvm-readobj prints a PIE's type as
    /// "DYN (Shared object file)"; the PIE flag in DT_FLAGS_1 is what tells it from a library.
    /// </summary>
    [Fact]
    public void A_libc_executable_is_a_PIE_with_glibcs_loader()
    {
        RequireSamples();
        string text = sdk.ReadElf(LibcApp, "-h", "-l", "-d");
        Assert.Contains("DYN (", text);
        Assert.Matches(new Regex(@"\(FLAGS_1\).*\bPIE\b"), text);
        Assert.Contains("/lib64/ld-linux-x86-64.so.2", text);
        Assert.Contains("Shared library: [libc.so.6]", text);
    }

    /// <summary>A static library holds its global symbols.</summary>
    [Fact]
    public void A_static_library_holds_its_symbols()
    {
        RequireSamples();
        Assert.Matches(new Regex(@"GLOBAL\s+DEFAULT\s+\d+\s+static_mul\b"), sdk.ReadElf(Archive, "-s"));
    }

    /// <summary>
    /// A portable .NET project receives the shared libraries of its .asmproj references under
    /// runtimes/&lt;rid&gt;/native/ (linux-x64 here, the samples' one RID), each listed in its
    /// deps.json, where the .NET host looks for them.
    /// </summary>
    [Fact]
    public void A_dotnet_project_receives_the_libraries_of_its_references()
    {
        RequireSamples();
        string native = Path.Combine(NetOutput, "runtimes", "linux-x64", "native");
        Assert.True(File.Exists(Path.Combine(native, "libhello.so")), "libhello.so missing in " + native);
        Assert.True(File.Exists(Path.Combine(native, "libhellolibc.so")), "libhellolibc.so missing in " + native);
        string deps = File.ReadAllText(Path.Combine(NetOutput, "Hello.Net.deps.json"));
        Assert.Contains("\"runtimes/linux-x64/native/libhello.so\"", deps);
        Assert.Contains("\"runtimes/linux-x64/native/libhellolibc.so\"", deps);
    }

    /// <summary>
    /// From a multi-targeted library, a .NET project receives the libc build: NuGet's nearest
    /// framework to net10.0 is netstandard2.0, which is libc.
    /// </summary>
    [Fact]
    public void A_dotnet_project_receives_the_libc_build_of_a_multi_targeted_library()
    {
        RequireSamples();
        Assert.Equal(File.ReadAllBytes(LibraryLibc), File.ReadAllBytes(Path.Combine(NetOutput, "runtimes", "linux-x64", "native", "libhello.so")));
    }

    /// <summary>A Debug build has DWARF: DebugSymbols=true comes from Microsoft.NET.Sdk, as in a .csproj.</summary>
    [Fact]
    public void A_Debug_build_has_DWARF()
    {
        CommandResult build = sdk.Once("build Hello.App Debug",
            () => sdk.Dotnet("build", "samples/Hello.App/Hello.App.asmproj", "-c", "Debug"));
        Assert.True(build.ExitCode == 0, build.Tail());
        Assert.Contains(".debug_info", sdk.ReadElf(Repo.Sample("Hello.App", "bin", "Debug", "freestanding", "linux-x64", "hello-app"), "-S"));
    }
}
