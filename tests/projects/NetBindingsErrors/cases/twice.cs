using AsmFoundation.Interop;

namespace NetBindingsErrors;

/// <summary>Takes add.asm's functions first.</summary>
[AsmLibrary("apiproto", Files = ["add.asm"])]
internal static partial class First
{
}

/// <summary>ASM0142: takes add.asm again, without AliasBinding.</summary>
[AsmLibrary("apiproto", Files = ["add.asm"])]
internal static partial class Second
{
}
