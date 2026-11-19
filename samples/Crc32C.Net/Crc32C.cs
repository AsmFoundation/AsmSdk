using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

// Every native signature below uses blittable types only (uint, byte*, nuint, int), so the
// runtime's marshalling layer has nothing to do; turning it off makes that a compile-time rule.
[assembly: DisableRuntimeMarshalling]

namespace Samples.Hashing;

/// <summary>A code path of libcrc32c: what <see cref="Crc32C.Path"/> reports and what the per-path overload forces.</summary>
public enum Crc32CPath
{
    /// <summary>A 256-entry table, one byte per step. Any CPU; the slowest path by far.</summary>
    Portable = 1,

    /// <summary>
    /// The CPU's CRC-32C instruction (SSE4.2's <c>crc32</c> on x64, the CRC32 extension's
    /// <c>crc32cx</c> on ARM64), 8 bytes per step on one dependency chain: each step waits for the
    /// previous one, so the instruction idles part of the time.
    /// </summary>
    Hardware = 2,

    /// <summary>
    /// Three CRC-32C instruction chains over three lanes of each block, merged with a carry-less
    /// multiply (PCLMULQDQ on x64, PMULL on ARM64): about three times <see cref="Hardware"/> on
    /// large inputs. Inputs under 192 bytes take the single chain.
    /// </summary>
    HardwareThreeWay = 3,
}

/// <summary>
/// CRC-32C (Castagnoli: iSCSI, ext4, SCTP, gRPC) computed by libcrc32c, an assembly library built by
/// AsmFoundation.Sdk for Windows, Linux (glibc and musl) and macOS, x64 and ARM64: crc32c.dll,
/// libcrc32c.so or libcrc32c.dylib, whichever this process's platform loads.
/// </summary>
/// <remarks>
/// <para>
/// The checksum convention is zlib's and System.IO.Hashing's: <see cref="HashToUInt32"/> starts a
/// checksum, and <see cref="Append(uint, ReadOnlySpan{byte})"/> continues one, so appending b to
/// the checksum of a gives the checksum of a followed by b.
/// </para>
/// <para>
/// The library picks its fastest path for this CPU on its first call; <see cref="Path"/> says which.
/// A portable .NET app carries every platform's build under runtimes/&lt;rid&gt;/native/ and the .NET
/// host loads the right one; loading throws <see cref="DllNotFoundException"/> on a platform none
/// of the seven builds serves (32-bit x86, for example), at the first call rather than at type load.
/// </para>
/// </remarks>
public static unsafe partial class Crc32C
{
    /// <summary>
    /// The library's name: [LibraryImport] probes crc32c.dll, libcrc32c.so or libcrc32c.dylib (each
    /// OS's naming), where deps.json's runtimeTargets say this platform's build is, then next to
    /// this assembly, then the usual paths.
    /// </summary>
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
    /// <exception cref="DllNotFoundException">libcrc32c could not be loaded (no build for this platform, or the file is missing).</exception>
    public static uint HashToUInt32(ReadOnlySpan<byte> source) => Append(0, source);

    /// <summary>Continues a checksum over more bytes, on the fastest path this CPU supports.</summary>
    /// <param name="crc">0 for a new checksum, or the result of an earlier call to continue it.</param>
    /// <param name="source">The next bytes; may be empty (the checksum is then returned unchanged).</param>
    /// <returns>The checksum of everything so far.</returns>
    /// <exception cref="DllNotFoundException">libcrc32c could not be loaded (no build for this platform, or the file is missing).</exception>
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
    /// library's path functions check nothing: on such a CPU they would fault (SIGILL, or an
    /// illegal-instruction exception on Windows) and end the process.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="path"/> is not a defined path.</exception>
    /// <exception cref="DllNotFoundException">libcrc32c could not be loaded.</exception>
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
                Crc32CPath.Hardware => AppendHardware(crc, data, length),
                _ => AppendHardwareThreeWay(crc, data, length),
            };
        }
    }

    /// <summary>Whether this CPU can run a path, as .NET sees it.</summary>
    /// <param name="path">The path.</param>
    /// <returns>
    /// True when the path's instructions are available; false for an undefined value. x64 needs
    /// SSE4.2 (and PCLMULQDQ for the 3-way path), ARM64 the CRC32 extension (and the AES extension,
    /// which carries PMULL). .NET's view can be narrower than the CPU's (DOTNET_EnableSSE42=0 turns
    /// SSE4.2 off for .NET), which only makes this more conservative than the library's own check.
    /// </returns>
    public static bool IsSupported(Crc32CPath path) => path switch
    {
        Crc32CPath.Portable => true,
        Crc32CPath.Hardware => Sse42.X64.IsSupported || Crc32.Arm64.IsSupported,
        Crc32CPath.HardwareThreeWay => (Sse42.X64.IsSupported && Pclmulqdq.IsSupported)
                                       || (Crc32.Arm64.IsSupported && System.Runtime.Intrinsics.Arm.Aes.IsSupported),
        _ => false,
    };

    /// <summary>
    /// The path <see cref="Append(uint, ReadOnlySpan{byte})"/> runs on this CPU, chosen once by the
    /// library (cpuid on x64; the ID registers on Linux ARM64, IsProcessorFeaturePresent on Windows
    /// ARM64; the 3-way path on every Apple silicon Mac).
    /// </summary>
    /// <exception cref="DllNotFoundException">libcrc32c could not be loaded.</exception>
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

    /// <summary>int crc32c_implementation(void): 1 portable, 2 hardware, 3 hardware + 3-way.</summary>
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

    /// <summary>The single-chain path (crc32c_append_hardware); the CRC instruction required, not checked.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_append_hardware")]
    private static partial uint AppendHardware(uint crc, byte* data, nuint length);

    /// <summary>The 3-way path (crc32c_append_hardware_3way); the CRC instruction and the carry-less multiply required, not checked.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport(Library, EntryPoint = "crc32c_append_hardware_3way")]
    private static partial uint AppendHardwareThreeWay(uint crc, byte* data, nuint length);
}
