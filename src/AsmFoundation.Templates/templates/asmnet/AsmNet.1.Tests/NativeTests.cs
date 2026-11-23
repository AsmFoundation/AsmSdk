using System.Runtime.InteropServices;

namespace AsmNet._1.Tests;

/// <summary>Tests the assembly library AsmNet.1.Native against C#'s own arithmetic.</summary>
public class NativeTests
{
    /// <summary>add_ints adds as C# does, wrapping at the ends of the int range.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    [Theory]
    [InlineData(40, 2)]
    [InlineData(0, 0)]
    [InlineData(-7, 3)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MinValue, -1)]
    public void Add_ints_adds_like_csharp(int a, int b) =>
        Assert.Equal(unchecked(a + b), Native.AddInts(a, b));
}

/// <summary>
/// The functions of the assembly library AsmNet.1.Native, declared for the tests. .NET finds it by
/// name in the test output: AsmNet.1.Native.dll, libAsmNet.1.Native.so or libAsmNet.1.Native.dylib.
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
