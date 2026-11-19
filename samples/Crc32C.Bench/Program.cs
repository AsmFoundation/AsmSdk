using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// CallBenchmarks' imports pass only blittable types, as Crc32C.Net's do; with runtime marshalling
// off, LibraryImport emits the same direct P/Invoke, so the two measure the same kind of call.
[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]

namespace Samples.Hashing.Bench;

/// <summary>
/// Starts BenchmarkDotNet on <see cref="Crc32CBenchmarks"/> and <see cref="CallBenchmarks"/>, or only
/// checks the paths with --check.
/// </summary>
internal static class Program
{
    /// <summary>Runs the benchmarks (any BenchmarkDotNet arguments pass through), or the check alone.</summary>
    /// <param name="args">
    /// "--check" to verify every path and exit; otherwise BenchmarkDotNet's own arguments, such as
    /// --filter '*' to run both classes without the interactive menu.
    /// </param>
    /// <returns>0 on success; 1 when a path disagrees with the managed checksum.</returns>
    private static int Main(string[] args)
    {
        if (args is ["--check"])
        {
            // A quick smoke test (CI runs it): every path agrees at every benchmark size.
            string? problem = Crc32CBenchmarks.Check();
            Console.WriteLine(problem ?? $"OK: every path agrees with BitOperations.Crc32C; dispatcher path: {Crc32C.Path}");
            return problem is null ? 0 : 1;
        }

        // In process: the benchmark runs in this process instead of a generated and rebuilt
        // project, so it needs no SDK at run time and works in a runtime-only container. The
        // native library is the same either way; only the managed side is jitted in place.
        IConfig config = DefaultConfig.Instance
            .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
            .AddColumn(new ThroughputColumn())
            .WithOrderer(new BenchmarkDotNet.Order.DefaultOrderer(BenchmarkDotNet.Order.SummaryOrderPolicy.Declared));
        BenchmarkSwitcher.FromTypes([typeof(Crc32CBenchmarks), typeof(CallBenchmarks)]).Run(args, config);
        return 0;
    }
}

/// <summary>
/// CRC-32C at five input sizes: libcrc32c's dispatcher, its three paths one by one, and the
/// managed baseline (the loop a C# developer writes with BitOperations.Crc32C).
/// </summary>
/// <remarks>
/// The managed baseline uses the same SSE4.2 instruction as the Sse42 path, 8 bytes per step, so
/// the two should be close: the 3-way path's advantage comes from running three independent chains,
/// not from the instruction. That makes this a fair test of what assembly adds.
/// </remarks>
public class Crc32CBenchmarks
{
    /// <summary>
    /// The sizes timed: a small record, a short message (where the cost of the call itself and the
    /// work are about equal), a typical message, a large buffer, a file chunk.
    /// </summary>
    public static readonly int[] Sizes = [64, 256, 1024, 64 * 1024, 1024 * 1024];

    /// <summary>The input of the current size (random bytes, fixed seed).</summary>
    private byte[] _data = [];

    /// <summary>The input size in bytes.</summary>
    [ParamsSource(nameof(SizeValues))]
    public int Size { get; set; }

    /// <summary>The values of <see cref="Size"/>, for BenchmarkDotNet.</summary>
    public static IEnumerable<int> SizeValues => Sizes;

    /// <summary>Creates the input, and refuses to time anything if a path is wrong.</summary>
    /// <exception cref="InvalidOperationException">A path disagrees with the managed checksum.</exception>
    [GlobalSetup]
    public void Setup()
    {
        _data = Input(Size);
        if (Check() is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <summary>The managed loop: BitOperations.Crc32C on 8-byte words, then on single bytes.</summary>
    /// <returns>The checksum.</returns>
    [Benchmark(Baseline = true, Description = "C# BitOperations.Crc32C")]
    public uint Managed() => ManagedCrc32C(_data);

    /// <summary>libcrc32c's dispatcher (the 3-way path on a CPU with PCLMULQDQ).</summary>
    /// <returns>The checksum.</returns>
    [Benchmark(Description = "asm crc32c_append (dispatcher)")]
    public uint Dispatcher() => Crc32C.HashToUInt32(_data);

    /// <summary>The 3-way path called directly.</summary>
    /// <returns>The checksum.</returns>
    [Benchmark(Description = "asm sse42 3-way")]
    public uint ThreeWay() => Crc32C.Append(0, _data, Crc32CPath.Sse42ThreeWay);

    /// <summary>The single-chain crc32 path.</summary>
    /// <returns>The checksum.</returns>
    [Benchmark(Description = "asm sse42 1 chain")]
    public uint OneChain() => Crc32C.Append(0, _data, Crc32CPath.Sse42);

    /// <summary>The table path.</summary>
    /// <returns>The checksum.</returns>
    [Benchmark(Description = "asm table")]
    public uint Table() => Crc32C.Append(0, _data, Crc32CPath.Portable);

    /// <summary>Checks every path that this CPU runs against the managed checksum, at every size.</summary>
    /// <returns>Null when all agree; otherwise what disagreed.</returns>
    public static string? Check()
    {
        foreach (int size in Sizes)
        {
            byte[] data = Input(size);
            uint expected = ManagedCrc32C(data);
            foreach (Crc32CPath path in Enum.GetValues<Crc32CPath>().Where(Crc32C.IsSupported))
            {
                uint actual = Crc32C.Append(0, data, path);
                if (actual != expected)
                {
                    return $"{path} at {size} bytes: 0x{actual:X8}, managed 0x{expected:X8}";
                }
            }

            if (Crc32C.HashToUInt32(data) != expected)
            {
                return $"the dispatcher at {size} bytes disagrees";
            }
        }

        return null;
    }

    /// <summary>
    /// CRC-32C in C#: BitOperations.Crc32C (SSE4.2's crc32 where available) over 8-byte words,
    /// then single bytes, with the same pre- and post-inversion as the library.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The checksum.</returns>
    public static uint ManagedCrc32C(ReadOnlySpan<byte> data)
    {
        uint crc = ~0u;
        while (data.Length >= 8)
        {
            crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }

        return ~crc;
    }

    /// <summary>Random bytes with a fixed seed, so every run times the same input.</summary>
    /// <param name="size">How many bytes.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Input(int size)
    {
        byte[] data = new byte[size];
        new Random(size).NextBytes(data);
        return data;
    }
}

/// <summary>
/// The price of one call into libcrc32c with nothing to checksum, with and without the GC
/// transition: what Crc32C.Net's 4 KiB SuppressGCTransition limit saves on every short input.
/// </summary>
/// <remarks>
/// The benchmark declares its own imports of crc32c_append, so the wrapper needs no public API
/// that exists only to be measured. libcrc32c.so is in this program's folder: the reference to
/// Crc32C.Net brings it along.
/// </remarks>
public unsafe partial class CallBenchmarks
{
    /// <summary>An ordinary call: the thread switches to preemptive mode and back around it.</summary>
    /// <returns>The checksum of nothing: 0.</returns>
    [Benchmark(Baseline = true, Description = "call, with the GC transition")]
    public uint WithTransition() => AppendWithTransition(0, null, 0);

    /// <summary>The same call without the switch, as Crc32C.Net makes it for inputs up to 4 KiB.</summary>
    /// <returns>The checksum of nothing: 0.</returns>
    [Benchmark(Description = "call, SuppressGCTransition")]
    public uint WithoutTransition() => AppendWithoutTransition(0, null, 0);

    /// <summary>crc32c_append through an ordinary P/Invoke.</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes (null: there are none).</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport("crc32c", EntryPoint = "crc32c_append")]
    private static partial uint AppendWithTransition(uint crc, byte* data, nuint length);

    /// <summary>crc32c_append without the GC transition (it returns at once for an empty input).</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes (null: there are none).</param>
    /// <param name="length">Their count.</param>
    /// <returns>The continued checksum.</returns>
    [LibraryImport("crc32c", EntryPoint = "crc32c_append")]
    [SuppressGCTransition]
    private static partial uint AppendWithoutTransition(uint crc, byte* data, nuint length);
}

/// <summary>A GB/s column: the input size divided by the mean time of each benchmark.</summary>
internal sealed class ThroughputColumn : IColumn
{
    /// <inheritdoc/>
    public string Id => nameof(ThroughputColumn);

    /// <inheritdoc/>
    public string ColumnName => "GB/s";

    /// <inheritdoc/>
    public bool AlwaysShow => true;

    /// <inheritdoc/>
    public ColumnCategory Category => ColumnCategory.Statistics;

    /// <inheritdoc/>
    public int PriorityInCategory => 0;

    /// <inheritdoc/>
    public bool IsNumeric => true;

    /// <inheritdoc/>
    public UnitType UnitType => UnitType.Dimensionless;

    /// <inheritdoc/>
    public string Legend => "Gigabytes (10^9 bytes) checksummed per second: Size / Mean";

    /// <inheritdoc/>
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <summary>Shown only in summaries whose cases have a Size: the call benchmarks have none.</summary>
    /// <param name="summary">The run's summary.</param>
    /// <returns>True when at least one case has parameters (a Size).</returns>
    public bool IsAvailable(Summary summary) => summary.BenchmarksCases.Any(c => c.HasParameters);

    /// <summary>The throughput of one benchmark case.</summary>
    /// <param name="summary">The run's summary.</param>
    /// <param name="benchmarkCase">The case.</param>
    /// <returns>GB/s with two decimals, or "?" when the case has no result.</returns>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        double? meanNs = summary[benchmarkCase]?.ResultStatistics?.Mean;
        // The call benchmarks have no Size parameter: no throughput to show.
        object? size = benchmarkCase.HasParameters ? benchmarkCase.Parameters["Size"] : null;
        return meanNs is > 0 && size is int bytes ? (bytes / meanNs.Value).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "?";
    }

    /// <summary>The throughput of one benchmark case (BenchmarkDotNet's style argument is not needed).</summary>
    /// <param name="summary">The run's summary.</param>
    /// <param name="benchmarkCase">The case.</param>
    /// <param name="style">The summary style (ignored).</param>
    /// <returns>See <see cref="GetValue(Summary, BenchmarkCase)"/>.</returns>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => GetValue(summary, benchmarkCase);
}
