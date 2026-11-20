using System.Runtime.InteropServices;

namespace AsmQa.Interop;

/// <summary>
/// QaLibc, an optional native component: built for the x64 RIDs only, so on ARM64 the file does not
/// exist. <see cref="IsAvailable"/> asks once with <see cref="NativeLibrary.TryLoad(string, System.Reflection.Assembly, DllImportSearchPath?, out nint)"/>
/// instead of letting the first call throw <see cref="DllNotFoundException"/>.
/// </summary>
public static partial class QaLibc
{
    /// <summary>The library name of every declaration below.</summary>
    private const string Library = "qalibc";

    /// <summary>Whether this platform has QaLibc (x64 RIDs: true; ARM64: false).</summary>
    public static bool IsAvailable { get; } = NativeLibrary.TryLoad(Library, typeof(QaLibc).Assembly, null, out _);

    /// <summary>A reversed copy of an ASCII string, allocated by the library's malloc and freed by its free.</summary>
    /// <param name="text">The text (ASCII: the copy reverses bytes, not code points).</param>
    /// <returns>The reversed copy.</returns>
    /// <exception cref="DllNotFoundException">QaLibc is not available on this platform.</exception>
    /// <exception cref="OutOfMemoryException">The library's malloc failed.</exception>
    public static string Reverse(string text)
    {
        using NativeUtf8String copy = DupReverse(text);
        return copy.IsInvalid ? throw new OutOfMemoryException("qa_dup_reverse: malloc failed") : copy.Read();
    }

    /// <summary>"value=&lt;n&gt;" formatted by the C library's snprintf into a caller-owned buffer.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The formatted text.</returns>
    /// <exception cref="DllNotFoundException">QaLibc is not available on this platform.</exception>
    public static string Format(long value)
    {
        Span<byte> buffer = stackalloc byte[64];
        int length = FormatNative(buffer, (nuint)buffer.Length, value);
        return System.Text.Encoding.ASCII.GetString(buffer[..length]);
    }

    /// <summary>Sorts in place with the C library's qsort and a comparator written in assembly.</summary>
    /// <param name="values">The values, sorted in place.</param>
    /// <exception cref="DllNotFoundException">QaLibc is not available on this platform.</exception>
    public static void Sort(Span<int> values) => SortNative(values, (nuint)values.Length);

    /// <summary>qa_dup_reverse, returning ownership as a <see cref="SafeHandle"/>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The copy, or an invalid handle when malloc failed.</returns>
    [LibraryImport(Library, EntryPoint = "qa_dup_reverse", StringMarshalling = StringMarshalling.Utf8)]
    private static partial NativeUtf8String DupReverse(string text);

    /// <summary>qa_free: releases what qa_dup_reverse allocated (the same C library's heap).</summary>
    /// <param name="pointer">The allocation.</param>
    [LibraryImport(Library, EntryPoint = "qa_free")]
    internal static partial void Free(nint pointer);

    /// <summary>qa_format.</summary>
    /// <param name="buffer">The destination.</param>
    /// <param name="capacity">Its size.</param>
    /// <param name="value">The number.</param>
    /// <returns>The length snprintf reports.</returns>
    [LibraryImport(Library, EntryPoint = "qa_format")]
    private static partial int FormatNative(Span<byte> buffer, nuint capacity, long value);

    /// <summary>qa_sort_i32.</summary>
    /// <param name="values">The values.</param>
    /// <param name="count">How many.</param>
    [LibraryImport(Library, EntryPoint = "qa_sort_i32")]
    private static partial void SortNative(Span<int> values, nuint count);
}

/// <summary>
/// A string the asm library allocated: <see cref="SafeHandle"/> frees it with the library's own
/// <c>qa_free</c> exactly once, even if the caller forgets to dispose it (the finalizer does). Never
/// free such memory with <see cref="Marshal.FreeHGlobal"/> or <see cref="NativeMemory.Free"/>: on
/// Windows they are different heaps.
/// </summary>
public sealed class NativeUtf8String : SafeHandle
{
    /// <summary>The marshaller creates it; it starts invalid and receives the returned pointer.</summary>
    public NativeUtf8String()
        : base(0, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == 0;

    /// <summary>The text, copied out while the memory is still owned.</summary>
    /// <returns>The string.</returns>
    /// <exception cref="ObjectDisposedException">The handle was already released.</exception>
    public string Read()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        return Marshal.PtrToStringUTF8(handle)!;
    }

    /// <summary>Frees the memory through the library that allocated it.</summary>
    /// <returns>Always true.</returns>
    protected override bool ReleaseHandle()
    {
        QaLibc.Free(handle);
        return true;
    }
}
