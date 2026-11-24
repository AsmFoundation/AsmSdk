using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using Samples.Hashing;

// Crc32C.Browser (see the .csproj): libcrc32c's WebAssembly build behind Crc32C.Net, checked and
// timed against .NET's own CRC-32C and against the same algorithm in C#, in one engine. Main
// serves "verify" and "bench"; the page (index.html) and "node main.mjs hash <file>" call
// Page.Hash through [JSExport] instead, without Main.
string mode = args.Length > 0 ? args[0] : "verify";
Console.WriteLine($"Crc32C.Browser: {RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.FrameworkDescription}, {Compilation.Name}, libcrc32c path {Crc32C.Path}");
return mode switch
{
    "verify" => Checks.Run(),
    "bench" => Checks.Run() == 0 ? Bench.Run() : 1,
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Crc32C.Browser verify|bench");
    return 2;
}

/// <summary>How the C# side of this build runs: Mono's interpreter (a .NET WebAssembly app's default) or ahead-of-time compiled.</summary>
internal static class Compilation
{
#if WASM_AOT
    /// <summary>"AOT": published with RunAOTCompilation=true (WASM_AOT), the C# compiled to WebAssembly ahead of time.</summary>
    internal const string Name = "AOT";
#else
    /// <summary>"interpreter": the C# runs on Mono's interpreter, a .NET WebAssembly app's default.</summary>
    internal const string Name = "interpreter";
#endif
}

/// <summary>
/// The three implementations under test, one signature: zlib's convention (0 starts a checksum,
/// a previous result continues it).
/// </summary>
internal static class Implementations
{
    /// <summary>
    /// The three, named as the benchmark and the page show them, in this order: the asm library
    /// first, so that the ratios and the agreement check compare the others with it.
    /// </summary>
    internal static readonly (string Name, Func<uint, byte[], uint> Run)[] All =
    [
        ("asm (crc32c.wasm.s)", (crc, bytes) => Asm(crc, bytes)),
        (".NET BitOperations.Crc32C", (crc, bytes) => Dotnet(crc, bytes)),
        ("C# slicing-by-8", (crc, bytes) => Managed(crc, bytes)),
    ];

    /// <summary>libcrc32c (crc32c.wasm.s, slicing-by-8), through Crc32C.Net's [LibraryImport].</summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The continued checksum.</returns>
    internal static uint Asm(uint crc, ReadOnlySpan<byte> data) => Crc32C.Append(crc, data);

    /// <summary>
    /// .NET's own CRC-32C: BitOperations.Crc32C, eight bytes per call and then one, between the
    /// complements of zlib's convention (BitOperations updates the raw register, as the x64 crc32
    /// instruction does). On WebAssembly it has no instruction to use and runs its software path.
    /// </summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The continued checksum.</returns>
    internal static uint Dotnet(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
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

    /// <summary>The eight slicing-by-8 tables, T0..T7, 256 entries each, flat: the ones crc32c.wasm.s computes.</summary>
    private static readonly uint[] Tables = BuildTables();

    /// <summary>
    /// The same slicing-by-8 as crc32c.wasm.s, in C#: the fair rival, the algorithm being equal,
    /// so that the comparison measures the code and not only the method.
    /// </summary>
    /// <param name="crc">The checksum to continue.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The continued checksum.</returns>
    internal static uint Managed(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] t = Tables;
        crc = ~crc;
        while (data.Length >= 8)
        {
            uint one = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ crc;
            uint two = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            crc = t[7 * 256 + (one & 0xFF)] ^ t[6 * 256 + ((one >> 8) & 0xFF)]
                ^ t[5 * 256 + ((one >> 16) & 0xFF)] ^ t[4 * 256 + (one >> 24)]
                ^ t[3 * 256 + (two & 0xFF)] ^ t[2 * 256 + ((two >> 8) & 0xFF)]
                ^ t[1 * 256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>Computes T0 (the CRC of each byte, reflected polynomial 0x82F63B78) and T1..T7 from it.</summary>
    /// <returns>The 2,048 entries.</returns>
    private static uint[] BuildTables()
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c >> 1) ^ (0x82F63B78u & (0u - (c & 1)));
            }

            t[n] = c;
        }

        for (int i = 256; i < t.Length; i++)
        {
            t[i] = (t[i - 256] >> 8) ^ t[t[i - 256] & 0xFF];
        }

        return t;
    }
}

/// <summary>Correctness first: the three must agree before any of them is timed.</summary>
internal static class Checks
{
    /// <summary>
    /// The known vector, then every length 0-4096 at every alignment 0-7, continued from a nonzero
    /// checksum, then a chained append: all three implementations must give the same checksum.
    /// </summary>
    /// <returns>0 when everything agrees, 1 otherwise (with the first disagreement on standard error).</returns>
    internal static int Run()
    {
        uint known = Implementations.Asm(0, "123456789"u8);
        if (known != 0xE3069283)
        {
            Console.Error.WriteLine($"123456789 gives 0x{known:X8}, not 0xE3069283");
            return 1;
        }

        byte[] data = new byte[4096 + 8];
        new Random(42).NextBytes(data);
        for (int offset = 0; offset < 8; offset++)
        {
            for (int length = 0; length <= 4096; length++)
            {
                ReadOnlySpan<byte> slice = data.AsSpan(offset, length);
                uint asm = Implementations.Asm(0x12345678, slice);
                uint dotnet = Implementations.Dotnet(0x12345678, slice);
                uint managed = Implementations.Managed(0x12345678, slice);
                if (asm != dotnet || asm != managed)
                {
                    Console.Error.WriteLine($"offset {offset}, length {length}: asm 0x{asm:X8}, .NET 0x{dotnet:X8}, C# 0x{managed:X8}");
                    return 1;
                }
            }
        }

        uint chained = Implementations.Asm(Implementations.Asm(0, data.AsSpan(0, 1000)), data.AsSpan(1000, 3096));
        if (chained != Implementations.Dotnet(0, data.AsSpan(0, 4096)))
        {
            Console.Error.WriteLine("a chained append differs from one append");
            return 1;
        }

        Console.WriteLine("verified: 123456789 = 0xE3069283; asm, .NET and C# agree on lengths 0-4096 at alignments 0-7, and on a chained append");
        return 0;
    }
}

/// <summary>
/// The timing: paired rounds, each running the three implementations over the same buffer in a
/// rotating order, so that a slower or faster stretch of the machine falls on all of them; then
/// each one's median throughput and range, and the per-round ratios' medians.
/// </summary>
internal static class Bench
{
    /// <summary>The rounds per size: odd, so that the median is one of them.</summary>
    private const int Rounds = 11;

    /// <summary>How long each timed batch runs, about, so that the timer's resolution does not matter.</summary>
    private static readonly TimeSpan Batch = TimeSpan.FromMilliseconds(60);

    /// <summary>Runs the rounds for a short, a medium and a long input, and prints a table per size.</summary>
    /// <returns>0.</returns>
    internal static int Run()
    {
        foreach (int size in new[] { 64, 4096, 1 << 20 })
        {
            Measure(size);
        }

        return 0;
    }

    /// <summary>Times the three implementations on one input size and prints the results.</summary>
    /// <param name="size">The input's length in bytes.</param>
    private static void Measure(int size)
    {
        byte[] data = new byte[size];
        new Random(size).NextBytes(data);
        (string Name, Func<uint, byte[], uint> Run)[] contenders = Implementations.All;

        // Warm-up, and the number of calls per batch: enough to fill a batch on each contender.
        int[] calls = new int[contenders.Length];
        for (int c = 0; c < contenders.Length; c++)
        {
            calls[c] = Calibrate(contenders[c].Run, data);
        }

        var throughput = new double[contenders.Length][];
        for (int c = 0; c < contenders.Length; c++)
        {
            throughput[c] = new double[Rounds];
        }

        uint check = 0;
        for (int round = 0; round < Rounds; round++)
        {
            for (int step = 0; step < contenders.Length; step++)
            {
                int c = (round + step) % contenders.Length;     // rotate the order every round
                long start = Stopwatch.GetTimestamp();
                uint crc = 0;
                for (int i = 0; i < calls[c]; i++)
                {
                    crc = contenders[c].Run(crc, data);
                }

                double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                throughput[c][round] = (double)size * calls[c] / seconds / 1e6;   // MB/s
                check ^= crc;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{size} bytes, {Rounds} paired rounds of about {Batch.TotalMilliseconds} ms each (checksum fold 0x{check:X8}):");
        Console.WriteLine("| implementation | median MB/s | min | max |");
        Console.WriteLine("|---|---:|---:|---:|");
        for (int c = 0; c < contenders.Length; c++)
        {
            double[] sorted = [.. throughput[c].Order()];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {contenders[c].Name} | {sorted[Rounds / 2]:F1} | {sorted[0]:F1} | {sorted[^1]:F1} |"));
        }

        for (int c = 1; c < contenders.Length; c++)
        {
            double[] ratios = [.. Enumerable.Range(0, Rounds).Select(r => throughput[0][r] / throughput[c][r]).Order()];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"asm / {contenders[c].Name}: median {ratios[Rounds / 2]:F2}x (rounds {ratios[0]:F2}x to {ratios[^1]:F2}x)"));
        }
    }

    /// <summary>Warms an implementation up and finds how many calls fill one batch.</summary>
    /// <param name="run">The implementation.</param>
    /// <param name="data">The input.</param>
    /// <returns>The calls per batch (at least 1).</returns>
    private static int Calibrate(Func<uint, byte[], uint> run, byte[] data)
    {
        int calls = 1;
        while (true)
        {
            long start = Stopwatch.GetTimestamp();
            uint crc = 0;
            for (int i = 0; i < calls; i++)
            {
                crc = run(crc, data);
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed >= Batch / 4 || calls >= 1 << 26)
            {
                return Math.Max(1, (int)(calls * (Batch / elapsed)));
            }

            calls *= 2;
        }
    }
}

/// <summary>
/// What the page (index.html, through main.mjs) and "node main.mjs hash &lt;file&gt;" call: the
/// CRC-32C of a file's bytes by each implementation, with each one's throughput. A demonstration,
/// not the benchmark: one file, one pass per implementation (or a few, for a small file), no
/// paired rounds; "bench" measures.
/// </summary>
/// <remarks>
/// [SupportedOSPlatform("browser")]: JSExport exists on the browser runtime only, and the platform
/// analyzer (CA1416, an error under the samples' TreatWarningsAsErrors) wants the class to say so.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("browser")]
internal static partial class Page
{
    /// <summary>
    /// How long each implementation's timed runs last at least. A small file is hashed again until
    /// this much time has passed, so that the timer's resolution and one call's overhead do not
    /// decide the figure; a file whose first pass already takes this long is timed by that pass.
    /// </summary>
    private static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Checksums the bytes with the asm library, .NET's BitOperations.Crc32C and the C#
    /// slicing-by-8, and times each.
    /// </summary>
    /// <param name="data">The file's bytes: a JavaScript Uint8Array, copied into .NET by the interop.</param>
    /// <returns>
    /// JSON for the caller to show: <c>compilation</c> (interpreter or AOT), <c>path</c> (the
    /// library's code path), <c>bytes</c>, <c>agree</c> (whether all three checksums are equal),
    /// and <c>results</c>, one <c>{name, crc, mbps}</c> per implementation, the checksum as 8 hex
    /// digits and the throughput in MB/s (10^6 bytes per second).
    /// </returns>
    /// <remarks>
    /// Synchronous: the page's thread is busy until it returns, so the page says "Hashing…"
    /// first. The UI thread is the only one a default .NET WebAssembly app has. The JSON is built
    /// by hand, from fixed names and numbers only, so the app needs no serializer (and keeps no
    /// reflection that trimming or AOT would have to preserve).
    /// </remarks>
    /// <exception cref="InvalidOperationException">An implementation gave two checksums for the same bytes (see <see cref="Time"/>); the interop throws it into JavaScript as an Error.</exception>
    [JSExport]
    internal static string Hash(byte[] data)
    {
        var results = new List<string>();
        uint? first = null;
        bool agree = true;
        foreach ((string name, Func<uint, byte[], uint> run) in Implementations.All)
        {
            (uint crc, double megabytesPerSecond) = Time(run, data);
            first ??= crc;
            agree &= crc == first;
            results.Add(string.Create(CultureInfo.InvariantCulture,
                $$"""{"name":"{{name}}","crc":"{{crc:X8}}","mbps":{{megabytesPerSecond:F1}}}"""));
        }

        return string.Create(CultureInfo.InvariantCulture,
            $$"""{"compilation":"{{Compilation.Name}}","path":"{{Crc32C.Path}}","bytes":{{data.Length}},"agree":{{(agree ? "true" : "false")}},"results":[{{string.Join(",", results)}}]}""");
    }

    /// <summary>Checksums the data once, then again until <see cref="Minimum"/> has passed if that pass was quicker.</summary>
    /// <param name="run">The implementation.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The checksum, and the throughput of the timed passes in MB/s (0 for an empty file).</returns>
    /// <exception cref="InvalidOperationException">A later pass gave another checksum than the first: the implementation is not deterministic, so no figure of it can be trusted.</exception>
    private static (uint Crc, double MegabytesPerSecond) Time(Func<uint, byte[], uint> run, byte[] data)
    {
        long start = Stopwatch.GetTimestamp();
        uint crc = run(0, data);
        TimeSpan first = Stopwatch.GetElapsedTime(start);
        if (first >= Minimum)
        {
            return (crc, data.Length / first.TotalSeconds / 1e6);
        }

        // The first pass was the warm-up (the interpreter's, or the library's table fill); the
        // passes after it are the ones timed.
        long passes = 0;
        start = Stopwatch.GetTimestamp();
        TimeSpan elapsed;
        do
        {
            if (run(0, data) != crc)
            {
                throw new InvalidOperationException("the same bytes gave two different checksums");
            }

            passes++;
            elapsed = Stopwatch.GetElapsedTime(start);
        }
        while (elapsed < Minimum);

        return (crc, data.Length * (double)passes / elapsed.TotalSeconds / 1e6);
    }
}
