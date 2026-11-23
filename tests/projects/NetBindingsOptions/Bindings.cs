using AsmFoundation.Interop;

namespace NetBindingsOptions;

/// <summary>strings.asm alone, public (a selection's class is flat by default).</summary>
[AsmLibrary("apiproto", Files = ["strings.asm"], Visibility = AsmVisibility.Public)]
public static partial class Text
{
}

/// <summary>Every file of simd/, with its span overloads back on (the assembly turned them off).</summary>
[AsmLibrary("apiproto", Folder = "simd", SpanOverloads = true)]
internal static partial class Vector
{
}

/// <summary>A second copy of add.asm's functions (the default class keeps them too).</summary>
[AsmLibrary("apiproto", Files = ["add"], AliasBinding = true)]
internal static partial class Alias
{
}

/// <summary>A copy of simd/sum's functions that inherits the assembly's "no span overloads".</summary>
[AsmLibrary("apiproto", Files = ["simd/sum"], AliasBinding = true)]
internal static partial class NoSpans
{
}
