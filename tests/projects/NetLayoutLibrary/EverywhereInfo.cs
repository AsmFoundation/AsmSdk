using System.Runtime.InteropServices;

namespace NetLayoutLibrary;

/// <summary>
/// What Everywhere's library answers on this machine, for a program that references this class
/// library and never the asm project itself (NetLayoutThrough, NetLayoutPackage).
/// </summary>
public static partial class EverywhereInfo
{
    /// <summary>
    /// The line NetConsumer prints, so that both routes are checked against the same
    /// expectation: "&lt;RuntimeInformation.RuntimeIdentifier&gt; &lt;ew_add(40, 2)&gt;
    /// &lt;ew_platform()&gt; &lt;ew_os()&gt;".
    /// </summary>
    /// <returns>The line; ew_platform() names the RID the loaded library was built for.</returns>
    /// <exception cref="DllNotFoundException">
    /// No everywhere library where the .NET host looks: the build or the publish lost it (the
    /// failure a single-file publish produced before the consumer targets marked asm libraries
    /// AssetType=native).
    /// </exception>
    public static string Describe() =>
        $"{RuntimeInformation.RuntimeIdentifier} {ew_add(40, 2)} {ew_platform()} {ew_os()}";

    /// <summary>a + b.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum.</returns>
    [LibraryImport("everywhere")]
    private static partial int ew_add(int a, int b);

    /// <summary>The RID the library was built for, as a number (see Everywhere.asmproj).</summary>
    /// <returns>1 to 7.</returns>
    [LibraryImport("everywhere")]
    private static partial int ew_platform();

    /// <summary>100 + EverywhereStatic's per-platform answer.</summary>
    /// <returns>110 to 121.</returns>
    [LibraryImport("everywhere")]
    private static partial int ew_os();
}
