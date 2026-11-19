using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

// Every native signature below uses blittable types only (uint, byte*, nuint, int), so the
// runtime's marshalling layer has nothing to do; turning it off makes that a compile-time rule.
[assembly: DisableRuntimeMarshalling]

namespace Samples.Hashing;

/// <summary>A code path of libcrc32c: what <see cref="Crc32C.Path"/> reports and what the per-path overload forces.</summary>
public enum Crc32CPath
{
    /// <summary>A 256-entry table, one byte per step. Any x86-64 CPU; the slowest path by far.</summary>
    Portable = 1,

    /// <summary>
    /// SSE4.2's <c>crc32</c> instruction, 8 bytes per step on one dependency chain: about 8 bytes
    /// every 3 cycles, because each step waits for the previous one.
    /// </summary>
    Sse42 = 2,

    /// <summary>
    /// Three <c>crc32</c> chains over three lanes of each block, merged with PCLMULQDQ: about three
    /// times <see cref="Sse42"/> on large inputs. Inputs under 192 bytes take the single chain.
    /// </summary>
    Sse42ThreeWay = 3,
}

/// <summary>
/// CRC-32C (Castagnoli: iSCSI, ext4, SCTP, gRPC) computed by libcrc32c.so, an x86-64 assembly
/// library built by AsmFoundation.Sdk. Linux x64 only.
/// </summary>
/// <remarks>
/// <para>
/// The checksum convention is zlib's and System.IO.Hashing's: <see cref="HashToUInt32"/> starts a
/// checksum, and <see cref="Append(uint, ReadOnlySpan{byte})"/> continues one, so appending b to
/// the checksum of a gives the checksum of a followed by b.
/// </para>
/// <para>
/// The library picks its fastest path for this CPU on its first call; <see cref="Path"/> says which.
/// Loading it throws <see cref="DllNotFoundException"/> on anything but Linux x64 (it is an ELF
/// shared object), at the first call rather than at type load.
/// </para>
/// </remarks>
public static unsafe partial class Crc32C
{
    /// <summary>The library's name: [LibraryImport] probes libcrc32c.so next to this assembly, then the usual paths.</summary>
    private const string Library = "crc32c";

    /// <summary>
    /// The longest input that skips the GC transition (bytes). Under it, a call takes well under a
    /// microsecond even on the single-chain path, so the GC can afford to wait for it; longer inputs
    /// keep the transition, so a GC is never held up by a long checksum.
    /// </summary>
    /// <remarks>
    /// The transition (cooperative to preemptive mode and back) costs a few nanoseconds per call,
    /// which matters for the short inputs a checksum often gets: a header, a 64-byte record.
    /// </remarks>
    private const int NoTransitionLimit = 4096;

    /// <summary>The checksum of <paramref name="source"/>, from the start.</summary>
    /// <param name="source">The bytes to checksum; may be empty (the checksum of nothing is 0).</param>
    /// <returns>The CRC-32C of <paramref name="source"/>: 0xE3069283 for the ASCII bytes "123456789".</returns>
    /// <exception cref="DllNotFoundException">libcrc32c.so could not be loaded (not Linux x64, or the file is missing).</exception>
    public static uint HashToUInt32(ReadOnlySpan<byte> source) => Append(0, source);

    /// <summary>Continues a checksum over more bytes, on the fastest path this CPU supports.</summary>
    /// <param name="crc">0 for a new checksum, or the result of an earlier call to continue it.</param>
    /// <param name="source">The next bytes; may be empty (the checksum is then returned unchanged).</param>
    /// <returns>The checksum of everything so far.</returns>
    /// <exception cref="DllNotFoundException">libcrc32c.so could not be loaded (not Linux x64, or the file is missing).</exception>
    public static uint Append(uint crc, ReadOnlySpan<byte> source)
    {
        // fixed on an empty span gives a null pointer; the library reads nothing when the length
        // is 0, so that is safe and saves a branch here.
        fixed (byte* data = source)
        {
            return source.Length <= NoTransitionLimit
                ? AppendShort(crc, data, (nuint)source.Length)
                : AppendLong(crc, data, (nuint)source.Length);
        }
    }

    /// <summary>
    /// Continues a checksum on one particular path, for tests that check each path and benchmarks
    /// that time each one. Programs call <see cref="Append(uint, ReadOnlySpan{byte})"/>.
    /// </summary>
    /// <param name="crc">0 for a new checksum, or the result of an earlier call to continue it.</param>
    /// <param name="source">The next bytes; may be empty.</param>
    /// <param name="path">The path to run.</param>
    /// <returns>The checksum of everything so far; every path returns the same value.</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// The CPU (as .NET sees it) lacks an instruction the path needs. Checked here because the
    /// library's path functions check nothing: on such a CPU they would raise SIGILL and end the
    /// process.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="path"/> is not a defined path.</exception>
    /// <exception cref="DllNotFoundException">libcrc32c.so could not be loaded.</exception>
    public static uint Append(uint crc, ReadOnlySpan<byte> source, Crc32CPath path)
    {
        if (!IsSupported(path))
        {
            throw Enum.IsDefined(path)
                ? new PlatformNotSupportedException($"This CPU cannot run the {path} path of libcrc32c.")
                : new ArgumentOutOfRangeException(nameof(path), path, "Not a libcrc32c path.");
        }

        fixed (byte* data = source)
        {
            nuint length = (nuint)source.Length;
            return path switch
            {
                Crc32CPath.Portable => AppendPortable(crc, data, length),
                Crc32CPath.Sse42 => AppendSse42(crc, data, length),
                _ => AppendSse42ThreeWay(crc, data, length),
            };
        }
    }

    /// <summary>Whether this CPU can run a path, as .NET sees it.</summary>
    /// <param name="path">The path.</param>
    /// <returns>
    /// True when the path's instructions are available; false for an undefined value. .NET's view
    /// can be narrower than the CPU's (DOTNET_EnableSSE42=0 turns SSE4.2 off for .NET), which only
    /// makes this more conservative than the library's own cpuid check.
    /// </returns>
    public static bool IsSupported(Crc32CPath path) => path switch
    {
        Crc32CPath.Portable => true,
        Crc32CPath.Sse42 => Sse42.X64.IsSupported,
        Crc32CPath.Sse42ThreeWay => Sse42.X64.IsSupported && Pclmulqdq.IsSupported,
        _ => false,
    };

    /// <summary>The path <see cref="Append(uint, ReadOnlySpan{byte})"/> runs on this CPU, chosen once by the library (cpuid).</summary>
    /// <exception cref="DllNotFoundException">libcrc32c.so could not be loaded.</exception>
    public static Crc32CPath Path => (Crc32CPath)Implementation();

    /// <summary>uint32_t crc32c_append(uint32_t crc, const void *data, size_t length), without the GC transition.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes (null only with a zero length).</param>
    /// <param name="length">Their count; at most <see cref="NoTransitionLimit"/>.</param>
    /// <returns>The continued checksum.</returns>
    /// <remarks>
    /// SuppressGCTransition is valid here because the function never blocks, never calls back into
    /// .NET and returns quickly for this length; the caller keeps longer inputs on <see cref="AppendLong"/>.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "crc32c_append")]
    [SuppressGCTransition]
    private static partial uint AppendShort(uint crc, byte* data, nuint length);

    /// <summary>uint32_t crc32c_append(uint32_t crc, const void *data, size_t length), with the GC transition.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_append")]
    private static partial uint AppendLong(uint crc, byte* data, nuint length);

    /// <summary>int crc32c_implementation(void): 1 portable, 2 sse42, 3 sse42 + 3-way.</summary>
    /// <returns>The path crc32c_append runs on this CPU.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_implementation")]
    private static partial int Implementation();

    /// <summary>The table path (crc32c_append_portable).</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_append_portable")]
    private static partial uint AppendPortable(uint crc, byte* data, nuint length);

    /// <summary>The single-chain crc32 path (crc32c_append_sse42); SSE4.2 required, not checked.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_append_sse42")]
    private static partial uint AppendSse42(uint crc, byte* data, nuint length);

    /// <summary>The 3-way path (crc32c_append_sse42_3way); SSE4.2 and PCLMULQDQ required, not checked.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_append_sse42_3way")]
    private static partial uint AppendSse42ThreeWay(uint crc, byte* data, nuint length);
}
