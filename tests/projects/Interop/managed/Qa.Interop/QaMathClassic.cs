using System.Runtime.InteropServices;

namespace AsmQa.Interop;

/// <summary>
/// QaMath through classic <see cref="DllImportAttribute"/> P/Invoke: the runtime builds the
/// marshalling stubs when a method is first called, so non-blittable shapes work without generated
/// code (arrays, managed delegates, <c>ref</c> structs, <c>[MarshalAs]</c> strings and bools).
/// Footgun: these stubs need runtime code generation; NativeAOT supports the common shapes, but the
/// trimmer and the AOT analyzer cannot see into them as they can into <see cref="QaMath"/>.
/// </summary>
public static class QaMathClassic
{
    /// <summary>The fold step a managed delegate supplies to <see cref="Reduce"/>.</summary>
    /// <param name="acc">The accumulator.</param>
    /// <param name="value">The next value.</param>
    /// <returns>The new accumulator.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long ReduceOp(long acc, int value);

    /// <summary>a + b, with the calling convention spelled out (Cdecl is the platform default on x64 and ARM64).</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The 32-bit sum.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_add", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Add(int a, int b);

    /// <summary>The sum of an array, which the runtime pins and passes as a pointer.</summary>
    /// <param name="values">The values.</param>
    /// <param name="count">How many.</param>
    /// <returns>The 64-bit sum.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_sum_i64")]
    public static extern long SumI64(long[] values, nuint count);

    /// <summary>Code points, with the string marshalled as UTF-8 by <see cref="UnmanagedType.LPUTF8Str"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of code points.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_utf8_count")]
    public static extern long Utf8Count([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    /// <summary>Upper-cases ASCII in place in an array the runtime pins, so the changes are visible after the call.</summary>
    /// <param name="text">The bytes.</param>
    /// <param name="length">How many.</param>
    /// <returns>How many changed.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_upper_ascii")]
    public static extern nuint UpperAscii([In, Out] byte[] text, nuint length);

    /// <summary>
    /// Folds through a managed delegate, which the runtime turns into a callable function pointer
    /// (a thunk) for the call's duration. The delegate may be a closure.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <param name="count">How many.</param>
    /// <param name="op">The fold step.</param>
    /// <returns>The final accumulator.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_reduce")]
    public static extern long Reduce(int[] values, nuint count, ReduceOp op);

    /// <summary>
    /// Folds through a raw function pointer: for a delegate converted with
    /// <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}(TDelegate)"/>, whose caller must keep
    /// the delegate alive while the pointer is in use.
    /// </summary>
    /// <param name="values">The values.</param>
    /// <param name="count">How many.</param>
    /// <param name="op">The fold step's address.</param>
    /// <returns>The final accumulator.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_reduce")]
    public static extern long ReducePointer(int[] values, nuint count, nint op);

    /// <summary>The dot product of two structs passed by <c>ref</c>.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The dot product.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_dot3")]
    public static extern long Dot3(ref Vec3i a, ref Vec3i b);

    /// <summary>Division with out parameters.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <param name="quotient">The quotient.</param>
    /// <param name="remainder">The remainder.</param>
    /// <returns>0, or -1 when <paramref name="b"/> is 0.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_divmod")]
    public static extern int DivMod(int a, int b, out int quotient, out int remainder);

    /// <summary>Whether a number is even, a 1-byte bool (<see cref="UnmanagedType.I1"/>; the default would be 4 bytes).</summary>
    /// <param name="value">The number.</param>
    /// <returns>True when even.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_is_even")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsEven(long value);

    /// <summary>A 16-byte struct returned by value through a classic stub.</summary>
    /// <param name="x">The first value.</param>
    /// <param name="y">The second value.</param>
    /// <returns><c>{ x, y }</c>.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_make_quad")]
    public static extern Quad16 MakeQuad(long x, long y);

    /// <summary>a*b + c*d through a classic stub.</summary>
    /// <param name="a">An integer.</param>
    /// <param name="b">A double.</param>
    /// <param name="c">An integer.</param>
    /// <param name="d">A double.</param>
    /// <returns>a*b + c*d.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [DllImport("qamath", EntryPoint = "qa_mix")]
    public static extern double Mix(long a, double b, long c, double d);
}
