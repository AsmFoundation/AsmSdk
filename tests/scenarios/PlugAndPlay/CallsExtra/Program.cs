using System.Runtime.InteropServices;

// Calls add_ints of the library the asmlib template created (Demo.Extra), with numbers the
// asmnet app does not use, so the line printed can only come from this program.
Console.WriteLine($"Demo.Extra add_ints(1000, 337) = {Extra.AddInts(1000, 337)}");

/// <summary>
/// The asmlib template's export, from the library Demo.Extra (Demo.Extra.dll,
/// libDemo.Extra.so or libDemo.Extra.dylib, found by .NET's naming rules).
/// </summary>
internal static partial class Extra
{
    /// <summary>add_ints: a + b, computed in assembly by the asmlib template's code.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum, wrapping on overflow like C#'s unchecked addition.</returns>
    [LibraryImport("Demo.Extra", EntryPoint = "add_ints")]
    internal static partial int AddInts(int a, int b);
}
