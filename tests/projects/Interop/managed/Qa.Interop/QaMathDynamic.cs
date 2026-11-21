using System.Runtime.InteropServices;

namespace AsmQa.Interop;

/// <summary>
/// QaMath loaded by hand: <see cref="NativeLibrary.Load(string, System.Reflection.Assembly, DllImportSearchPath?)"/>
/// finds the library exactly as P/Invoke would (deps.json's runtimes/&lt;rid&gt;/native/ entries
/// included), and <see cref="NativeLibrary.GetExport"/> gives raw addresses, called here two ways:
/// as unmanaged function pointers (no marshalling, no allocation) and as delegates made by
/// <see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}(nint)"/>.
/// </summary>
public static unsafe class QaMathDynamic
{
    /// <summary>The loaded library's handle; never freed (the library lives as long as the process).</summary>
    /// <exception cref="DllNotFoundException">Thrown by the type initializer when there is no qamath build for this platform.</exception>
    public static readonly nint Handle = NativeLibrary.Load("qamath", typeof(QaMathDynamic).Assembly, null);

    /// <summary>qa_add as an unmanaged function pointer.</summary>
    public static readonly delegate* unmanaged<int, int, int> Add =
        (delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(Handle, "qa_add");

    /// <summary>qa_many as an unmanaged function pointer: eight 64-bit arguments.</summary>
    public static readonly delegate* unmanaged<long, long, long, long, long, long, long, long, long> Many =
        (delegate* unmanaged<long, long, long, long, long, long, long, long, long>)NativeLibrary.GetExport(Handle, "qa_many");

    /// <summary>qa_make_pair as an unmanaged function pointer returning an 8-byte struct.</summary>
    public static readonly delegate* unmanaged<int, int, Pair> MakePair =
        (delegate* unmanaged<int, int, Pair>)NativeLibrary.GetExport(Handle, "qa_make_pair");

    /// <summary>
    /// qa_make_quad as an unmanaged function pointer returning a 16-byte struct: the JIT supplies the
    /// hidden return buffer on Windows x64 here too.
    /// </summary>
    public static readonly delegate* unmanaged<long, long, Quad16> MakeQuad =
        (delegate* unmanaged<long, long, Quad16>)NativeLibrary.GetExport(Handle, "qa_make_quad");

    /// <summary>qa_mix through a delegate the runtime builds over the raw address.</summary>
    public static readonly MixFunction Mix =
        Marshal.GetDelegateForFunctionPointer<MixFunction>(NativeLibrary.GetExport(Handle, "qa_mix"));

    /// <summary>qa_make_quad through a delegate: a struct return by a runtime-built stub.</summary>
    public static readonly MakeQuadFunction MakeQuadDelegate =
        Marshal.GetDelegateForFunctionPointer<MakeQuadFunction>(NativeLibrary.GetExport(Handle, "qa_make_quad"));

    /// <summary>The shape of qa_mix for <see cref="Mix"/>.</summary>
    /// <param name="a">An integer.</param>
    /// <param name="b">A double.</param>
    /// <param name="c">An integer.</param>
    /// <param name="d">A double.</param>
    /// <returns>a*b + c*d.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double MixFunction(long a, double b, long c, double d);

    /// <summary>The shape of qa_make_quad for <see cref="MakeQuadDelegate"/>.</summary>
    /// <param name="x">The first value.</param>
    /// <param name="y">The second value.</param>
    /// <returns><c>{ x, y }</c>.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate Quad16 MakeQuadFunction(long x, long y);

    /// <summary>Whether an export exists, without throwing: how optional entry points are probed.</summary>
    /// <param name="name">The export's name.</param>
    /// <returns>True when the library exports it.</returns>
    public static bool HasExport(string name) => NativeLibrary.TryGetExport(Handle, name, out _);
}
