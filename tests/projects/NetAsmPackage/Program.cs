using System.Runtime.InteropServices;

// Everywhere's library is everywhere.dll on Windows and libeverywhere.so / .dylib elsewhere:
// [LibraryImport("everywhere")] finds each by .NET's own naming rules, from deps.json's
// runtimeTargets in a portable build and from the app folder in a RID-specific one, both as
// NuGet resolves the Test.Everywhere package, or from where its BinariesCopyPath put it.
Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier} {Native.ew_add(40, 2)} {Native.ew_platform()} {Native.ew_os()}");

/// <summary>The exports of Everywhere.asmproj.</summary>
internal static partial class Native
{
    /// <summary>a + b.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum.</returns>
    [LibraryImport("everywhere")]
    internal static partial int ew_add(int a, int b);

    /// <summary>The RID the library was built for, as a number (see Everywhere.asmproj).</summary>
    /// <returns>1 to 7.</returns>
    [LibraryImport("everywhere")]
    internal static partial int ew_platform();

    /// <summary>100 + the static library's per-platform answer.</summary>
    /// <returns>110 to 121.</returns>
    [LibraryImport("everywhere")]
    internal static partial int ew_os();
}
