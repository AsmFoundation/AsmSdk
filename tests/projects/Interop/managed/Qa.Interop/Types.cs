using System.Runtime.InteropServices;

namespace AsmQa.Interop;

/// <summary>
/// qamath.h's <c>vec3i</c>: three 32-bit integers, 12 bytes. Passed by pointer (<c>in</c> or
/// <c>ref</c>), so its size never decides how it travels.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vec3i
{
    /// <summary>The first component.</summary>
    public int X;

    /// <summary>The second component.</summary>
    public int Y;

    /// <summary>The third component.</summary>
    public int Z;
}

/// <summary>
/// qamath.h's <c>pair</c>: two 32-bit integers, 8 bytes. Passed and returned BY VALUE in one
/// integer register on every OS (Windows x64, System V, AAPCS64), so it exercises struct passing
/// without the hidden-pointer rules.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Pair
{
    /// <summary>The first value (the low half of the register).</summary>
    public int A;

    /// <summary>The second value (the high half).</summary>
    public int B;
}

/// <summary>
/// qamath.h's <c>quad</c>: two 64-bit integers, 16 bytes. Returned by value it travels differently
/// per OS: rax:rdx on System V, x0:x1 on ARM64, and through a hidden return pointer on Windows x64,
/// which .NET supplies on its own; the asm must know (qamath.asm's qa_make_quad).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Quad16
{
    /// <summary>The first value.</summary>
    public long X;

    /// <summary>The second value.</summary>
    public long Y;
}
