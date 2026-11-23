using System.Runtime.InteropServices;

// Calls add_ints, a function written in assembly in AsmNet.1.Native, and prints its answer.
int sum = Native.AddInts(40, 2);
Console.WriteLine($"add_ints(40, 2) = {sum}");

/// <summary>
/// The functions of the assembly library AsmNet.1.Native. .NET finds it by name:
/// AsmNet.1.Native.dll on Windows, libAsmNet.1.Native.so on Linux, libAsmNet.1.Native.dylib on macOS.
/// </summary>
internal static partial class Native
{
    /// <summary>add_ints: a + b, computed in assembly.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum, wrapping on overflow like C#'s unchecked addition.</returns>
    [LibraryImport("AsmNet.1.Native", EntryPoint = "add_ints")]
    internal static partial int AddInts(int a, int b);
}
