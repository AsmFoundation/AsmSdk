namespace AsmNet._1.Tests;

/// <summary>
/// Tests the assembly library AsmNet.1.Native against C#'s own arithmetic, through the C#
/// bindings the SDK writes from its PROTO lines (AsmNet._1.Native.Add for add.asm).
/// </summary>
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
        Assert.Equal(unchecked(a + b), AsmNet._1.Native.Add.AddInts(a, b));
}
