using AsmFoundation.Interop;

namespace NetBindingsOptions;

/// <summary>A copy of strings.asm's functions, public (a selection's class is flat by default).</summary>
[AsmLibrary("apiproto", Files = ["strings.asm"], Visibility = AsmVisibility.Public)]
public static partial class Text
{
}

/// <summary>A copy of every file of simd/, with span overloads back on (the assembly turned them off for copies).</summary>
[AsmLibrary("apiproto", Folder = "simd", SpanOverloads = true)]
internal static partial class Vector
{
}

/// <summary>A second copy of simd/sum's functions (Vector has them): AliasBinding; inherits the assembly's "no span overloads".</summary>
[AsmLibrary("apiproto", Files = ["simd/sum"], AliasBinding = true)]
internal static partial class NoSpans
{
}

/// <summary>add.asm's functions, copied first.</summary>
[AsmLibrary("apiproto", Files = ["add"])]
internal static partial class MathA
{
}

/// <summary>add.asm's functions again: allowed by AliasBinding (between the app's own classes).</summary>
[AsmLibrary("apiproto", Files = ["add"], AliasBinding = true)]
internal static partial class MathB
{
}

/// <summary>A non-static partial class: its copies are instance methods, virtual as asked.</summary>
[AsmLibrary("apiproto", Files = ["add"], AliasBinding = true, Virtual = true)]
internal partial class Calculator
{
}

/// <summary>Overrides a virtual copy: the call through the base class lands here.</summary>
internal sealed class FixedCalculator : Calculator
{
    /// <summary>Always 7, whatever the arguments.</summary>
    /// <param name="a">Ignored.</param>
    /// <param name="b">Ignored.</param>
    /// <returns>7.</returns>
    internal override int AddInts(int a, int b) => 7;
}

/// <summary>Overrides the baseline's virtual AddInts (the baseline is instance, virtual, protected internal).</summary>
internal sealed class FixedApi : Proto.Api
{
    /// <summary>Always 99.</summary>
    /// <param name="a">Ignored.</param>
    /// <param name="b">Ignored.</param>
    /// <returns>99.</returns>
    protected internal override int AddInts(int a, int b) => 99;
}
