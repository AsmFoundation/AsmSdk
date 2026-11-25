using AsmFoundation.Interop;

namespace NetBindingsErrors;

/// <summary>ASM0146: the Default* options shape the library's baseline; a class cannot set them.</summary>
[AsmLibrary("apiproto", DefaultNesting = AsmNesting.Flat)]
internal static partial class Wrong
{
}
