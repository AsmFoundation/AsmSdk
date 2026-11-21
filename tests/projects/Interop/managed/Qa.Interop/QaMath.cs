using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AsmQa.Interop;

/// <summary>
/// QaMath through source-generated P/Invoke (<see cref="LibraryImportAttribute"/>): the marshalling
/// code is generated at compile time, so it is trim- and NativeAOT-safe. The library name "qamath"
/// resolves to qamath.dll, libqamath.so or libqamath.dylib, found through deps.json's
/// runtimes/&lt;rid&gt;/native/ entries in a portable app, or next to the app in a RID-specific one.
/// </summary>
public static unsafe partial class QaMath
{
    /// <summary>The library name every declaration below uses.</summary>
    private const string Library = "qamath";

    /// <summary>a + b (wraps on overflow, as C's int32 does).</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The 32-bit sum.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_add")]
    public static partial int Add(int a, int b);

    /// <summary>
    /// <see cref="Add"/> without the GC transition: the call stays in cooperative mode, which saves
    /// a few nanoseconds and is only safe for short functions that never block or call back.
    /// </summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The 32-bit sum.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_add")]
    [SuppressGCTransition]
    public static partial int AddNoTransition(int a, int b);

    /// <summary>The sum of a span, which the generated code pins and passes as a pointer.</summary>
    /// <param name="values">The values.</param>
    /// <param name="count">How many to add: at most <c>values.Length</c>.</param>
    /// <returns>The 64-bit sum (wrapping).</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_sum_i64")]
    public static partial long SumI64(ReadOnlySpan<long> values, nuint count);

    /// <summary>a*b + c*d, with integer and floating-point arguments interleaved.</summary>
    /// <param name="a">An integer.</param>
    /// <param name="b">A double.</param>
    /// <param name="c">An integer.</param>
    /// <param name="d">A double.</param>
    /// <returns>a*b + c*d.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_mix")]
    public static partial double Mix(long a, double b, long c, double d);

    /// <summary>1*a1 + ... + 8*a8: eight arguments, some of them on the stack on x64.</summary>
    /// <param name="a1">Argument 1.</param>
    /// <param name="a2">Argument 2.</param>
    /// <param name="a3">Argument 3.</param>
    /// <param name="a4">Argument 4.</param>
    /// <param name="a5">Argument 5 (on the stack on Windows).</param>
    /// <param name="a6">Argument 6 (on the stack on Windows).</param>
    /// <param name="a7">Argument 7 (on the stack on x64).</param>
    /// <param name="a8">Argument 8 (on the stack on x64).</param>
    /// <returns>The weighted sum.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_many")]
    public static partial long Many(long a1, long a2, long a3, long a4, long a5, long a6, long a7, long a8);

    /// <summary>The dot product of two structs passed by reference (<c>in</c>: a pointer, read-only).</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>a.X*b.X + a.Y*b.Y + a.Z*b.Z in 64 bits.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_dot3")]
    public static partial long Dot3(in Vec3i a, in Vec3i b);

    /// <summary>A + B of an 8-byte struct passed by value.</summary>
    /// <param name="pair">The pair.</param>
    /// <returns>The 64-bit sum.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_pair_sum")]
    public static partial long PairSum(Pair pair);

    /// <summary>An 8-byte struct returned by value.</summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <returns><c>{ a, b }</c>.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_make_pair")]
    public static partial Pair MakePair(int a, int b);

    /// <summary>A 16-byte struct returned by value (a hidden return pointer on Windows x64).</summary>
    /// <param name="x">The first value.</param>
    /// <param name="y">The second value.</param>
    /// <returns><c>{ x, y }</c>.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_make_quad")]
    public static partial Quad16 MakeQuad(long x, long y);

    /// <summary>
    /// Folds values through a C# function the asm calls back: a function pointer to an
    /// <see cref="UnmanagedCallersOnlyAttribute"/> method, no delegate and no allocation.
    /// </summary>
    /// <param name="values">The values, pinned for the call.</param>
    /// <param name="count">How many to fold.</param>
    /// <param name="op">The fold step, called once per value with the accumulator.</param>
    /// <returns>The final accumulator (starting from 0).</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_reduce")]
    public static partial long Reduce(ReadOnlySpan<int> values, nuint count, delegate* unmanaged<long, int, long> op);

    /// <summary>
    /// The library's static version string. Declared as <c>byte*</c> on purpose: a <c>string</c>
    /// return would make the generated code FREE the pointer after copying it, and this one points
    /// into the library's read-only data.
    /// </summary>
    /// <returns>A NUL-terminated UTF-8 string owned by the library.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_version")]
    private static partial byte* VersionPointer();

    /// <summary>The library's version string, which names the RID it was built for.</summary>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    public static string Version => Marshal.PtrToStringUTF8((nint)VersionPointer())!;

    /// <summary>The code points of a string, which the generated code passes as UTF-8.</summary>
    /// <param name="text">The text (converted to a NUL-terminated UTF-8 copy for the call).</param>
    /// <returns>The number of code points.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_utf8_count", StringMarshalling = StringMarshalling.Utf8)]
    public static partial long Utf8Count(string text);

    /// <summary>Upper-cases the ASCII letters of a caller-owned buffer in place.</summary>
    /// <param name="text">The bytes, changed in place.</param>
    /// <param name="length">How many bytes to process.</param>
    /// <returns>How many bytes changed.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_upper_ascii")]
    public static partial nuint UpperAscii(Span<byte> text, nuint length);

    /// <summary>Truncating division with two out parameters and a status code.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <param name="quotient">a / b, when the result is 0.</param>
    /// <param name="remainder">a % b, when the result is 0.</param>
    /// <returns>0, or -1 when <paramref name="b"/> is 0 (the library then leaves the out values unwritten).</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_divmod")]
    public static partial int DivMod(int a, int b, out int quotient, out int remainder);

    /// <summary>Whether a number is even: a C <c>bool</c>, one byte.</summary>
    /// <param name="value">The number.</param>
    /// <returns>True when even.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_is_even")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool IsEven(long value);

    /// <summary>Adds to the library's process-wide counter atomically (lock xadd, or LL/SC on ARM64).</summary>
    /// <param name="delta">What to add (0 reads it).</param>
    /// <returns>The counter after the addition.</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_counter_add")]
    public static partial long CounterAdd(long delta);

    /// <summary>Element-wise float addition in SSE or NEON.</summary>
    /// <param name="a">The first operands.</param>
    /// <param name="b">The second operands.</param>
    /// <param name="sum">The results.</param>
    /// <param name="count">How many elements: at most the shortest span's length.</param>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_add_f32")]
    public static partial void AddF32(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> sum, nuint count);

    /// <summary>The sum of squares, computed through a hidden helper of a linked asm static library.</summary>
    /// <param name="values">The values.</param>
    /// <param name="count">How many.</param>
    /// <returns>The 64-bit sum of squares (wrapping).</returns>
    /// <exception cref="DllNotFoundException">No qamath build for this platform.</exception>
    [LibraryImport(Library, EntryPoint = "qa_sum_squares")]
    public static partial long SumSquares(ReadOnlySpan<long> values, nuint count);
}

/// <summary>Fold steps the asm calls back into: C-callable static methods.</summary>
public static unsafe class QaCallbacks
{
    /// <summary>A pointer to <see cref="Sum"/>, for <see cref="QaMath.Reduce"/>.</summary>
    public static delegate* unmanaged<long, int, long> SumPointer => &Sum;

    /// <summary>A pointer to <see cref="Max"/>, for <see cref="QaMath.Reduce"/>.</summary>
    public static delegate* unmanaged<long, int, long> MaxPointer => &Max;

    /// <summary>acc + value.</summary>
    /// <param name="acc">The accumulator.</param>
    /// <param name="value">The next value.</param>
    /// <returns>The new accumulator.</returns>
    [UnmanagedCallersOnly]
    private static long Sum(long acc, int value) => acc + value;

    /// <summary>max(acc, value), treating the initial 0 as "no value yet" only through the data.</summary>
    /// <param name="acc">The accumulator.</param>
    /// <param name="value">The next value.</param>
    /// <returns>The larger of the two.</returns>
    [UnmanagedCallersOnly]
    private static long Max(long acc, int value) => Math.Max(acc, value);
}
