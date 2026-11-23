using System.Runtime.InteropServices;

// Calls the asm static library linked into this NativeAOT executable.
Console.WriteLine($"sl_mul(6, 7) = {Native.Mul(6, 7)}");

/// <summary>The Sectionless static library's function, linked in by NativeAOT.</summary>
internal static partial class Native
{
    /// <summary>sl_mul(a, b) of the sectionless static library: a * b.</summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second factor.</param>
    /// <returns>a * b.</returns>
    /// <remarks>
    /// "sectionless" is the library's AssemblyName: the consumer targets add a DirectPInvoke item
    /// of that name, so ILC calls sl_mul directly and the linker finds it in the archive. Without
    /// it (a JIT run of this program), the call would look for a shared library that does not
    /// exist and throw DllNotFoundException.
    /// </remarks>
    [LibraryImport("sectionless", EntryPoint = "sl_mul")]
    internal static partial int Mul(int a, int b);
}
