using AsmFoundation.Interop;

namespace NetStructsErrors;

/// <summary>
/// ASM0145: public, and it binds shapes.asm, whose point_set takes a point, a struct the
/// library's baseline (the namespace Apistruct) keeps internal. A public method of an internal
/// type's parameter would be CS0051 in the generated code; the generator reports it here, with
/// the fix.
/// </summary>
[AsmLibrary("apistruct", Files = ["shapes.asm"], Visibility = AsmVisibility.Public)]
public static partial class PublicShapes
{
}

/// <summary>The program: never runs (the build fails), but an Exe needs an entry point.</summary>
internal static class Program
{
    /// <summary>Does nothing.</summary>
    private static void Main()
    {
    }
}
