using System.Runtime.InteropServices;

// Calls add_ints, a function written in WebAssembly assembly in AsmWasmApp.1.Native, and prints
// its answer.
int sum = Native.AddInts(40, 2);
Console.WriteLine($"add_ints(40, 2) = {sum}");

/// <summary>
/// The functions of the assembly library AsmWasmApp.1.Native, linked into this app's WebAssembly
/// runtime when the app builds. The name in [LibraryImport] is the library's AssemblyName, as on
/// every other platform, whatever its archive is called.
/// </summary>
internal static partial class Native
{
    /// <summary>add_ints: a + b, computed in WebAssembly assembly.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum, wrapping on overflow like C#'s unchecked addition.</returns>
    [LibraryImport("AsmWasmApp.1.Native", EntryPoint = "add_ints")]
    internal static partial int AddInts(int a, int b);
}
