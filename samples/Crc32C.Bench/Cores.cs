using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Samples.Hashing.Bench;

/// <summary>
/// The core-scaling run, "--cores": the C# loop and libcrc32c's dispatcher on one core and on
/// several at once, every thread pinned to a logical CPU of its own, in paired rounds.
/// </summary>
/// <remarks>
/// <para>
/// BenchmarkDotNet times one call on one thread. This run answers what it cannot: does the
/// assembly's lead over C# hold, grow or shrink when every core checksums at once? On N cores, N
/// threads each checksum their own copy of the input over and over, the way a server checksums
/// independent requests on each of its cores, and a cell's figure is the total of its N streams.
/// Splitting one buffer across cores is a different question (one checksum's latency), and this
/// run does not ask it.
/// </para>
/// <para>
/// Pairing: every round times every cell (implementation, size, core count) once, and the order of
/// the two implementations alternates from cell to cell and round to round, so a noisy minute on a
/// shared machine lands on both sides. The summary gives each cell's median over the rounds with
/// its lowest and highest round; each ratio is the median of the per-round ratios, so it compares
/// figures taken minutes apart only through rounds that saw the same conditions.
/// </para>
/// <para>
/// Pinning ("--cpus") is what makes a core count mean anything on a hybrid CPU: unpinned, the OS
/// may move a thread to an efficiency core, or put two threads on one core's two hyperthreads,
/// where each gets about half the core. On Windows the run also turns power throttling (EcoQoS)
/// off for this process, which otherwise runs a process started without a window on efficiency
/// cores at about half speed. Keeping other processes off the pinned cores' second hyperthreads
/// is the caller's job: the results folders' harness scripts hold them with coreguard.
/// </para>
/// </remarks>
internal static unsafe partial class CoreScaling
{
    /// <summary>The command line this mode accepts, printed with any usage error.</summary>
    private const string Usage =
        "usage: Crc32C.Bench --cores <counts> [--cpus <list>] [--sizes <bytes list>] [--rounds N] [--seconds S] [--warmup S] [--json <file>]\n" +
        "  --cores 1,4          the core counts to time (one thread per core)\n" +
        "  --cpus 2,4,6,8       logical CPUs to pin the threads to, the first N for N cores (default: unpinned)\n" +
        "  --sizes 64,1048576   input sizes in bytes (default: the BenchmarkDotNet sizes)\n" +
        "  --rounds 12          paired rounds (default 12)\n" +
        "  --seconds 0.5        timed window of each cell in each round (default 0.5)\n" +
        "  --warmup 0.1         how long each cell's threads run before its window opens (default 0.1)\n" +
        "  --json <file>        also write every round's raw counts as JSON";

    /// <summary>
    /// ProcessPowerThrottling, the PROCESS_INFORMATION_CLASS value SetProcessInformation takes for
    /// power throttling (EcoQoS) settings.
    /// </summary>
    private const int ProcessPowerThrottling = 4;

    /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED: the one throttling knob that moves work to efficiency cores.</summary>
    private const uint PowerThrottlingExecutionSpeed = 1;

    /// <summary>
    /// A cell's threads add their batches' combined results in here once their window closes, so
    /// the JIT can never prove a checksum unused and drop the work being timed. Its value means
    /// nothing.
    /// </summary>
    private static long s_sink;

    /// <summary>The implementations a cell can time.</summary>
    private enum Implementation
    {
        /// <summary>The C# loop a developer writes first: BitOperations.Crc32C over 8-byte words (<see cref="Crc32CBenchmarks.ManagedCrc32C"/>).</summary>
        CSharp,

        /// <summary>libcrc32c's dispatcher through Crc32C.Net (<see cref="Crc32C.HashToUInt32"/>), what a C# caller of the library runs.</summary>
        Asm,
    }

    /// <summary>
    /// Runs the core-scaling comparison and prints its summary as Markdown.
    /// </summary>
    /// <param name="args">The arguments after "--cores": the core counts first, then the options in <see cref="Usage"/>.</param>
    /// <returns>
    /// 0 when every cell was timed; 1 when a path disagrees with the managed checksum (nothing is
    /// timed then) or a thread failed; 2 on a usage error.
    /// </returns>
    /// <remarks>
    /// Takes rounds times sizes times core counts times 2 cells of (warm-up + window) seconds,
    /// plus a few seconds of warm-up: about 2.5 minutes with the defaults. Each pinned CPU is busy
    /// for the whole window of every cell that uses it.
    /// </remarks>
    public static int Run(string[] args)
    {
        if (!TryParse(args, out Options? options, out string? usageError))
        {
            Console.Error.WriteLine(usageError);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // The same promise as the BenchmarkDotNet run: nothing is timed until every path agrees
        // with the managed checksum at every size.
        if (Crc32CBenchmarks.Check() is { } problem)
        {
            Console.Error.WriteLine($"not timing: {problem}");
            return 1;
        }

        // Without this a process started from a shell (no window of its own) is EcoQoS work on
        // Windows 11: on a hybrid CPU it lands on efficiency cores at about half speed, and its
        // threads inherit that even when pinned to a performance core.
        string throttling = OperatingSystem.IsWindows()
            ? (DisablePowerThrottling() ? "off for this process" : $"could not be turned off (error {Marshal.GetLastPInvokeError()})")
            : "not applicable";

        var started = DateTimeOffset.UtcNow;
        var header = new Header(
            Started: started.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            Cpu: CpuName(),
            LogicalCpus: Environment.ProcessorCount,
            Os: RuntimeInformation.OSDescription,
            Runtime: RuntimeInformation.FrameworkDescription,
            DispatcherPath: Crc32C.Path.ToString(),
            PinnedCpus: options.Cpus,
            PowerThrottling: throttling,
            PriorityClass: Process.GetCurrentProcess().PriorityClass.ToString(),
            Options: options);
        PrintHeader(header);

        try
        {
            // Warm-up, discarded: every implementation at every size on one thread, long enough
            // for tiered compilation to promote the batch loops, then a pause for its background
            // compiler to install the optimized code before round 1 times anything.
            foreach (int size in options.Sizes)
            {
                foreach (Implementation implementation in Enum.GetValues<Implementation>())
                {
                    RunCell(0, implementation, size, 1, options with { Seconds = 0.2 });
                }
            }

            Thread.Sleep(TimeSpan.FromSeconds(1));

            var cells = new List<CellResult>();
            for (int round = 1; round <= options.Rounds; round++)
            {
                var clock = Stopwatch.StartNew();
                for (int s = 0; s < options.Sizes.Length; s++)
                {
                    for (int c = 0; c < options.Cores.Length; c++)
                    {
                        // Alternate which implementation goes first, from cell to cell and from
                        // round to round, so neither always runs right after the other's heat.
                        Implementation[] order = (round + s + c) % 2 == 0
                            ? new[] { Implementation.CSharp, Implementation.Asm }
                            : new[] { Implementation.Asm, Implementation.CSharp };
                        foreach (Implementation implementation in order)
                        {
                            cells.Add(RunCell(round, implementation, options.Sizes[s], options.Cores[c], options));
                        }
                    }
                }

                Console.WriteLine($"round {round} of {options.Rounds} done in {clock.Elapsed.TotalSeconds:F1} s");
            }

            Console.WriteLine();
            PrintSummary(cells, options);

            if (options.JsonPath is { } path)
            {
                var document = new RunDocument(header, cells.Select(CellRecord.From).ToArray());
                File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
                Console.WriteLine();
                Console.WriteLine($"raw counts: {path}");
            }

            return 0;
        }
        catch (CellFailedException failure)
        {
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
    }

    /// <summary>
    /// Times one cell: N threads, each pinned (when CPUs were given) and checksumming its own copy
    /// of the input, inside one shared window.
    /// </summary>
    /// <param name="round">The round number, recorded with the result (0 for the warm-up).</param>
    /// <param name="implementation">What each thread runs.</param>
    /// <param name="size">The input size in bytes.</param>
    /// <param name="cores">How many threads, one per core.</param>
    /// <param name="options">The CPUs to pin to, the window and the warm-up.</param>
    /// <returns>Each thread's count of checksums and its own measure of the window.</returns>
    /// <exception cref="CellFailedException">A thread failed to pin itself, or its checksum was wrong.</exception>
    /// <remarks>
    /// The window opens when every thread is pinned, checked and warmed up (the start barrier) and
    /// closes when the coordinator raises the stop flag; each thread times its own window with
    /// Stopwatch, so the coordinator's coarse sleep only sets the window's rough length. Threads
    /// are new for every cell: a little start-up cost outside the window buys cells that share no
    /// state.
    /// </remarks>
    private static CellResult RunCell(int round, Implementation implementation, int size, int cores, Options options)
    {
        var control = new CellControl(cores + 1);
        var streams = new StreamResult[cores];
        var failures = new Exception?[cores];
        var threads = new Thread[cores];
        for (int i = 0; i < cores; i++)
        {
            // The lambda's own copies: the loop variable keeps changing after Start returns.
            int index = i;
            int cpu = options.Cpus.Length > 0 ? options.Cpus[i] : -1;
            threads[i] = new Thread(() => RunStream(index, cpu, implementation, size, options.WarmupSeconds, control, streams, failures))
            {
                IsBackground = true,
                Name = $"crc32c core {index}",
            };
            threads[i].Start();
        }

        control.Start.SignalAndWait();
        Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
        Volatile.Write(ref control.Stop, 1);
        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        if (failures.FirstOrDefault(f => f is not null) is { } failure)
        {
            throw new CellFailedException($"{implementation} at {size} bytes on {cores} cores: {failure.Message}", failure);
        }

        return new CellResult(round, implementation, size, cores, streams);
    }

    /// <summary>One thread of a cell: pin, check, warm up, wait for the window, then checksum until told to stop.</summary>
    /// <param name="index">The thread's slot in <paramref name="streams"/> and <paramref name="failures"/>.</param>
    /// <param name="cpu">The logical CPU to pin to, or -1 to stay unpinned.</param>
    /// <param name="implementation">What to run.</param>
    /// <param name="size">The input size in bytes.</param>
    /// <param name="warmupSeconds">How long to run before joining the start barrier.</param>
    /// <param name="control">The cell's start barrier and stop flag.</param>
    /// <param name="streams">Where this thread writes its result.</param>
    /// <param name="failures">Where this thread writes what went wrong instead; never thrown on this thread.</param>
    /// <remarks>
    /// An exception escaping a thread would end the process, and a thread that dies before the
    /// barrier would leave the coordinator waiting forever. So every failure is caught and
    /// recorded, and a thread that fails before signalling the barrier leaves it, which lets the
    /// others and the coordinator go on; the coordinator then reports the failure.
    /// </remarks>
    private static void RunStream(
        int index,
        int cpu,
        Implementation implementation,
        int size,
        double warmupSeconds,
        CellControl control,
        StreamResult[] streams,
        Exception?[] failures)
    {
        bool joined = false;
        try
        {
            if (cpu >= 0)
            {
                Pin(cpu);
            }

            // Allocated and filled by the pinned thread itself, so the input starts out in this
            // core's caches; every thread gets the same bytes (Input's fixed seed), so one
            // expected checksum serves them all.
            byte[] input = Crc32CBenchmarks.Input(size);
            uint expected = Crc32CBenchmarks.ManagedCrc32C(input);
            int batch = BatchSize(size);
            if (Batch(implementation, input, 1) != expected)
            {
                throw new InvalidOperationException($"checksum 0x{Batch(implementation, input, 1):X8}, expected 0x{expected:X8}");
            }

            long warmupEnd = Stopwatch.GetTimestamp() + (long)(warmupSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < warmupEnd)
            {
                Batch(implementation, input, batch);
            }

            control.Start.SignalAndWait();
            joined = true;

            long calls = 0;
            uint sink = 0;
            long start = Stopwatch.GetTimestamp();
            // The flag is read once per batch, not per call: a batch takes a few microseconds, so
            // the read costs nothing measurable and the window closes within a batch of the flag.
            while (Volatile.Read(ref control.Stop) == 0)
            {
                sink ^= Batch(implementation, input, batch);
                calls += batch;
            }

            long end = Stopwatch.GetTimestamp();

            // The checksum must still be right after the window: a thread that silently computed
            // garbage at speed must not be counted.
            uint after = Batch(implementation, input, 1);
            if (after != expected)
            {
                throw new InvalidOperationException($"checksum after the window 0x{after:X8}, expected 0x{expected:X8}");
            }

            Interlocked.Add(ref s_sink, sink);
            streams[index] = new StreamResult(cpu, calls, (end - start) / (double)Stopwatch.Frequency);
        }
        catch (Exception exception)
        {
            failures[index] = exception;
            if (!joined)
            {
                control.Start.RemoveParticipant();
            }
        }
    }

    /// <summary>
    /// How many checksums a thread runs between two reads of the stop flag: enough that a batch
    /// takes microseconds at 64 bytes, and one call from 64 KiB up (one call already takes
    /// microseconds there).
    /// </summary>
    /// <param name="size">The input size in bytes.</param>
    /// <returns>At least 1.</returns>
    private static int BatchSize(int size) => Math.Max(1, 65536 / size);

    /// <summary>Runs one batch of the given implementation.</summary>
    /// <param name="implementation">The C# loop or the assembly.</param>
    /// <param name="input">The bytes.</param>
    /// <param name="count">How many checksums.</param>
    /// <returns>The XOR of the checksums (the checksum itself when <paramref name="count"/> is 1).</returns>
    /// <remarks>
    /// The choice is made once per batch, so the timed loops below call their function directly:
    /// a delegate per call would add an indirect call to a 3 ns checksum.
    /// </remarks>
    private static uint Batch(Implementation implementation, byte[] input, int count) =>
        implementation == Implementation.CSharp ? CSharpBatch(input, count) : AsmBatch(input, count);

    /// <summary>The C# loop, <paramref name="count"/> times in a row on the same input.</summary>
    /// <param name="input">The bytes.</param>
    /// <param name="count">How many checksums.</param>
    /// <returns>The XOR of the checksums.</returns>
    /// <remarks>
    /// Each checksum is independent of the last, as in BenchmarkDotNet's loop, so the CPU may
    /// overlap successive short checksums; NoInlining keeps this loop one method that tiered
    /// compilation promotes on its own call count, the same way for both implementations.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint CSharpBatch(byte[] input, int count)
    {
        uint acc = 0;
        for (int i = 0; i < count; i++)
        {
            acc ^= Crc32CBenchmarks.ManagedCrc32C(input);
        }

        return acc;
    }

    /// <summary>libcrc32c's dispatcher, <paramref name="count"/> times in a row on the same input.</summary>
    /// <param name="input">The bytes.</param>
    /// <param name="count">How many checksums.</param>
    /// <returns>The XOR of the checksums.</returns>
    /// <remarks>
    /// Through Crc32C.Net, exactly as a C# caller reaches the library: without the GC transition
    /// up to 4 KiB, with it above.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint AsmBatch(byte[] input, int count)
    {
        uint acc = 0;
        for (int i = 0; i < count; i++)
        {
            acc ^= Crc32C.HashToUInt32(input);
        }

        return acc;
    }

    /// <summary>Pins the calling thread to one logical CPU.</summary>
    /// <param name="cpu">The logical CPU: below 64 on Windows (processor group 0), below 1024 on Linux.</param>
    /// <exception cref="InvalidOperationException">The OS refused the affinity (a CPU this process may not use, or one that does not exist).</exception>
    /// <exception cref="PlatformNotSupportedException">Neither Windows nor Linux: macOS offers no hard thread affinity.</exception>
    private static void Pin(int cpu)
    {
        if (OperatingSystem.IsWindows())
        {
            // GetCurrentThread's pseudo-handle means "the calling thread"; a zero return is the
            // failure signal (the previous mask is never zero).
            if (SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << cpu) == 0)
            {
                throw new InvalidOperationException($"SetThreadAffinityMask(CPU {cpu}) failed: error {Marshal.GetLastPInvokeError()}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            // A cpu_set_t of 1024 bits, as glibc and musl define it; pid 0 is the calling thread.
            byte* mask = stackalloc byte[128];
            new Span<byte>(mask, 128).Clear();
            mask[cpu / 8] = (byte)(1 << (cpu % 8));
            if (sched_setaffinity(0, 128, mask) != 0)
            {
                throw new InvalidOperationException($"sched_setaffinity(CPU {cpu}) failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
        else
        {
            throw new PlatformNotSupportedException("--cpus pins threads on Windows and Linux only");
        }
    }

    /// <summary>Turns power throttling (EcoQoS) off for this process, for its whole life.</summary>
    /// <returns>True when Windows accepted the setting; the error is then in Marshal.GetLastPInvokeError.</returns>
    /// <remarks>
    /// Threads created afterwards inherit it, which is every timed thread. It is not applied when
    /// the library loads, only in this timing mode: it takes performance cores that would
    /// otherwise go to foreground work.
    /// </remarks>
    private static bool DisablePowerThrottling()
    {
        // Version 1 of PROCESS_POWER_THROTTLING_STATE: control the execution-speed knob and set it
        // to 0, "never throttle".
        var state = new PowerThrottlingState { Version = 1, ControlMask = PowerThrottlingExecutionSpeed, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, (uint)sizeof(PowerThrottlingState)) != 0;
    }

    /// <summary>The CPU's marketing name, for the run's header.</summary>
    /// <returns>The name from the Windows registry or Linux's /proc/cpuinfo, or "unknown".</returns>
    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
            }

            if (OperatingSystem.IsLinux())
            {
                string? line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                return line?[(line.IndexOf(':') + 1)..].Trim() ?? "unknown";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The name only labels the run; a machine that hides it still gets timed.
        }

        return "unknown";
    }

    /// <summary>Prints what ran where, as the Markdown run report's opening lines.</summary>
    /// <param name="header">The run's setup.</param>
    private static void PrintHeader(Header header)
    {
        Options o = header.Options;
        Console.WriteLine("# Crc32C.Bench --cores: the C# loop and the assembly on one core and on several");
        Console.WriteLine();
        Console.WriteLine($"- started: {header.Started}");
        Console.WriteLine($"- CPU: {header.Cpu}, {header.LogicalCpus} logical CPUs");
        Console.WriteLine($"- OS: {header.Os}; runtime: {header.Runtime}");
        Console.WriteLine($"- the assembly: crc32c_append through Crc32C.Net, dispatcher path {header.DispatcherPath}");
        Console.WriteLine($"- the C# loop: BitOperations.Crc32C over 8-byte words");
        Console.WriteLine(o.Cpus.Length > 0
            ? $"- threads pinned to logical CPUs {string.Join(",", o.Cpus)} (thread i on the i-th)"
            : "- threads NOT pinned: the OS places them, so core counts are approximate");
        Console.WriteLine($"- power throttling: {header.PowerThrottling}; process priority class: {header.PriorityClass}");
        Console.WriteLine($"- {o.Rounds} paired rounds; each cell runs {o.WarmupSeconds:0.###} s of warm-up, then a {o.Seconds:0.###} s window");
        Console.WriteLine($"- core counts {string.Join(", ", o.Cores)}; sizes {string.Join(", ", o.Sizes.Select(SizeLabel))}");
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the medians over the rounds, each cell's spread, the paired ratios and the scaling
    /// from one core, as Markdown tables.
    /// </summary>
    /// <param name="cells">Every timed cell of every round (no warm-up).</param>
    /// <param name="options">The core counts and sizes, for the tables' order.</param>
    private static void PrintSummary(List<CellResult> cells, Options options)
    {
        int rounds = options.Rounds;
        CellResult[] Of(Implementation implementation, int size, int cores) =>
            cells.Where(c => c.Implementation == implementation && c.Size == size && c.Cores == cores).OrderBy(c => c.Round).ToArray();

        Console.WriteLine($"## Medians of {rounds} paired rounds");
        Console.WriteLine();
        Console.WriteLine("GB/s and calls/s are all cores together; \"per call\" is the time one call takes on its core while the others run.");
        Console.WriteLine();
        Console.WriteLine("| Size | Cores | C# GB/s | asm GB/s | C# calls/s | asm calls/s | C# per call | asm per call | asm speed / C# | asm faster in |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (int size in options.Sizes)
        {
            foreach (int cores in options.Cores)
            {
                CellResult[] cs = Of(Implementation.CSharp, size, cores);
                CellResult[] asm = Of(Implementation.Asm, size, cores);
                // Paired: each round's asm rate over the same round's C# rate.
                double[] ratios = asm.Zip(cs, (a, c) => a.CallsPerSecond / c.CallsPerSecond).ToArray();
                Console.WriteLine(
                    $"| {SizeLabel(size)} | {cores} | {Fmt(Median(cs.Select(c => c.GigabytesPerSecond)))} | {Fmt(Median(asm.Select(c => c.GigabytesPerSecond)))} | " +
                    $"{Rate(Median(cs.Select(c => c.CallsPerSecond)))} | {Rate(Median(asm.Select(c => c.CallsPerSecond)))} | " +
                    $"{Time(Median(cs.Select(c => c.NanosecondsPerCallPerCore)))} | {Time(Median(asm.Select(c => c.NanosecondsPerCallPerCore)))} | " +
                    $"{Median(ratios):F2}x | {ratios.Count(r => r > 1)} of {ratios.Length} |");
            }
        }

        Console.WriteLine();
        Console.WriteLine("## Spread: the lowest and the highest round, GB/s");
        Console.WriteLine();
        Console.WriteLine("| Size | Cores | C# | asm | asm speed / C#, per round |");
        Console.WriteLine("|---|---:|---:|---:|---:|");
        foreach (int size in options.Sizes)
        {
            foreach (int cores in options.Cores)
            {
                CellResult[] cs = Of(Implementation.CSharp, size, cores);
                CellResult[] asm = Of(Implementation.Asm, size, cores);
                double[] ratios = asm.Zip(cs, (a, c) => a.CallsPerSecond / c.CallsPerSecond).ToArray();
                Console.WriteLine(
                    $"| {SizeLabel(size)} | {cores} | {Fmt(cs.Min(c => c.GigabytesPerSecond))} to {Fmt(cs.Max(c => c.GigabytesPerSecond))} | " +
                    $"{Fmt(asm.Min(c => c.GigabytesPerSecond))} to {Fmt(asm.Max(c => c.GigabytesPerSecond))} | {ratios.Min():F2}x to {ratios.Max():F2}x |");
            }
        }

        if (options.Cores.Contains(1) && options.Cores.Length > 1)
        {
            Console.WriteLine();
            Console.WriteLine("## Scaling: N cores over 1 core, median of the per-round ratios");
            Console.WriteLine();
            int[] more = options.Cores.Where(c => c != 1).ToArray();
            Console.WriteLine("| Size | " + string.Join(" | ", more.SelectMany(n => new[] { $"C# x{n}", $"asm x{n}" })) + " |");
            Console.WriteLine("|---|" + string.Concat(Enumerable.Repeat("---:|", more.Length * 2)));
            foreach (int size in options.Sizes)
            {
                var row = new StringBuilder($"| {SizeLabel(size)} |");
                foreach (int n in more)
                {
                    foreach (Implementation implementation in Enum.GetValues<Implementation>())
                    {
                        // Within each round: the N-core total over the same round's one-core total.
                        double[] scaling = Of(implementation, size, n).Zip(Of(implementation, size, 1), (many, one) => many.CallsPerSecond / one.CallsPerSecond).ToArray();
                        row.Append($" {Median(scaling):F2}x |");
                    }
                }

                Console.WriteLine(row);
            }
        }
    }

    /// <summary>The median of a sequence (the mean of the middle two for an even count).</summary>
    /// <param name="values">At least one value.</param>
    /// <returns>The median.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="values"/> is empty.</exception>
    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new InvalidOperationException("no values");
        }

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Three significant figures, the precision the docs quote.</summary>
    /// <param name="value">A positive figure.</param>
    /// <returns>The figure, invariant culture.</returns>
    private static string Fmt(double value) =>
        value >= 100 ? value.ToString("F0", CultureInfo.InvariantCulture)
        : value >= 10 ? value.ToString("F1", CultureInfo.InvariantCulture)
        : value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>A rate with a k, M or G suffix, three significant figures.</summary>
    /// <param name="perSecond">Calls per second.</param>
    /// <returns>For example "76.9M" or "20.4k".</returns>
    private static string Rate(double perSecond) =>
        perSecond >= 1e9 ? Fmt(perSecond / 1e9) + "G"
        : perSecond >= 1e6 ? Fmt(perSecond / 1e6) + "M"
        : perSecond >= 1e3 ? Fmt(perSecond / 1e3) + "k"
        : Fmt(perSecond);

    /// <summary>A duration in ns, us or ms (ASCII, so a redirected report reads the same on every console code page).</summary>
    /// <param name="nanoseconds">The duration in nanoseconds.</param>
    /// <returns>For example "3.35 ns" or "48.1 us".</returns>
    private static string Time(double nanoseconds) =>
        nanoseconds >= 1e6 ? Fmt(nanoseconds / 1e6) + " ms"
        : nanoseconds >= 1e3 ? Fmt(nanoseconds / 1e3) + " us"
        : Fmt(nanoseconds) + " ns";

    /// <summary>A size as the docs write it: 64 B, 1 KiB, 1 MiB.</summary>
    /// <param name="size">Bytes.</param>
    /// <returns>The label.</returns>
    private static string SizeLabel(int size) =>
        size >= 1 << 20 && size % (1 << 20) == 0 ? $"{size >> 20} MiB"
        : size >= 1 << 10 && size % (1 << 10) == 0 ? $"{size >> 10} KiB"
        : $"{size} B";

    /// <summary>Parses the arguments after "--cores".</summary>
    /// <param name="args">The core counts, then options.</param>
    /// <param name="options">The parsed options, when the result is true.</param>
    /// <param name="error">What is wrong, when the result is false.</param>
    /// <returns>True when the command line is valid.</returns>
    private static bool TryParse(string[] args, [NotNullWhen(true)] out Options? options, [NotNullWhen(false)] out string? error)
    {
        options = null;
        error = null;
        if (args.Length == 0 || !TryInts(args[0], out int[]? cores) || cores.Length == 0 || cores.Any(c => c < 1))
        {
            error = "--cores needs a list of core counts, such as 1,4";
            return false;
        }

        int[] cpus = [];
        int[] sizes = Crc32CBenchmarks.Sizes;
        int rounds = 12;
        double seconds = 0.5, warmup = 0.1;
        string? json = null;
        for (int i = 1; i < args.Length; i += 2)
        {
            // Every option takes exactly one value, so options advance two at a time.
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            bool ok;
            switch (args[i])
            {
                case "--cpus":
                    ok = TryInts(value, out int[]? parsedCpus);
                    cpus = parsedCpus ?? [];
                    break;
                case "--sizes":
                    ok = TryInts(value, out int[]? parsedSizes) && parsedSizes.Length > 0 && parsedSizes.All(s => s > 0);
                    sizes = parsedSizes ?? sizes;
                    break;
                case "--rounds":
                    ok = int.TryParse(value, CultureInfo.InvariantCulture, out rounds) && rounds > 0;
                    break;
                case "--seconds":
                    ok = double.TryParse(value, CultureInfo.InvariantCulture, out seconds) && seconds > 0;
                    break;
                case "--warmup":
                    ok = double.TryParse(value, CultureInfo.InvariantCulture, out warmup) && warmup >= 0;
                    break;
                case "--json":
                    json = value;
                    ok = json is not null;
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok)
            {
                error = $"bad option or value: {args[i]} {value}";
                return false;
            }
        }

        if (cpus.Length > 0 && cpus.Length < cores.Max())
        {
            error = $"--cpus lists {cpus.Length} CPUs, but --cores asks for {cores.Max()} cores";
            return false;
        }

        if (cpus.Distinct().Count() != cpus.Length || cpus.Any(c => c < 0 || c >= (OperatingSystem.IsWindows() ? 64 : 1024)))
        {
            error = "--cpus needs distinct logical CPUs (below 64 on Windows, processor group 0)";
            return false;
        }

        options = new Options(cores, cpus, sizes, rounds, seconds, warmup, json);
        return true;
    }

    /// <summary>Parses a comma-separated list of integers.</summary>
    /// <param name="text">For example "1,4"; null fails.</param>
    /// <param name="values">The integers, when the result is true.</param>
    /// <returns>True when every item is an integer.</returns>
    private static bool TryInts(string? text, [NotNullWhen(true)] out int[]? values)
    {
        values = null;
        if (text is null)
        {
            return false;
        }

        var list = new List<int>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }

            list.Add(value);
        }

        values = [.. list];
        return true;
    }

    /// <summary>The JSON settings of the raw-counts file: readable, enums as names.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>What to run, parsed from the command line.</summary>
    /// <param name="Cores">The core counts to time, such as 1 and 4.</param>
    /// <param name="Cpus">The logical CPUs to pin threads to, the first N for N cores; empty: unpinned.</param>
    /// <param name="Sizes">The input sizes in bytes.</param>
    /// <param name="Rounds">How many paired rounds.</param>
    /// <param name="Seconds">How long each cell's window lasts, in each round.</param>
    /// <param name="WarmupSeconds">How long each cell's threads run before its window opens.</param>
    /// <param name="JsonPath">Where to write every round's raw counts, or null.</param>
    private sealed record Options(int[] Cores, int[] Cpus, int[] Sizes, int Rounds, double Seconds, double WarmupSeconds, string? JsonPath);

    /// <summary>The run's setup, printed first and kept in the raw-counts file.</summary>
    /// <param name="Started">When the run began, UTC.</param>
    /// <param name="Cpu">The CPU's name.</param>
    /// <param name="LogicalCpus">Environment.ProcessorCount.</param>
    /// <param name="Os">The OS description.</param>
    /// <param name="Runtime">The .NET runtime.</param>
    /// <param name="DispatcherPath">The path libcrc32c's dispatcher picked on this CPU.</param>
    /// <param name="PinnedCpus">The logical CPUs threads were pinned to; empty when unpinned.</param>
    /// <param name="PowerThrottling">Whether EcoQoS was turned off.</param>
    /// <param name="PriorityClass">The process priority class (the harness scripts raise it to High).</param>
    /// <param name="Options">The parsed options.</param>
    private sealed record Header(
        string Started,
        string Cpu,
        int LogicalCpus,
        string Os,
        string Runtime,
        string DispatcherPath,
        int[] PinnedCpus,
        string PowerThrottling,
        string PriorityClass,
        Options Options);

    /// <summary>One thread's share of one timed cell.</summary>
    /// <param name="Cpu">The logical CPU it was pinned to, or -1.</param>
    /// <param name="Calls">The checksums it completed in its window.</param>
    /// <param name="Seconds">Its window, as the thread itself measured it.</param>
    private sealed record StreamResult(int Cpu, long Calls, double Seconds);

    /// <summary>One timed cell of one round.</summary>
    /// <param name="Round">The round, from 1.</param>
    /// <param name="Implementation">What ran.</param>
    /// <param name="Size">The input size in bytes.</param>
    /// <param name="Cores">How many threads ran at once.</param>
    /// <param name="Streams">Each thread's count and window.</param>
    private sealed record CellResult(int Round, Implementation Implementation, int Size, int Cores, StreamResult[] Streams)
    {
        /// <summary>Checksums per second of all the cell's threads together: each thread's own rate, summed.</summary>
        /// <remarks>Summing rates, not calls over one shared time, keeps a thread that saw the stop flag a batch late from inflating the total.</remarks>
        public double CallsPerSecond => Streams.Sum(s => s.Calls / s.Seconds);

        /// <summary>Gigabytes (10^9 bytes) checksummed per second, all threads together.</summary>
        public double GigabytesPerSecond => CallsPerSecond * Size / 1e9;

        /// <summary>The time one call takes on its core while the cell's other cores run: cores divided by the total rate.</summary>
        public double NanosecondsPerCallPerCore => Cores * 1e9 / CallsPerSecond;
    }

    /// <summary>A cell as the raw-counts file stores it, with its derived figures.</summary>
    /// <param name="Round">The round.</param>
    /// <param name="Implementation">What ran.</param>
    /// <param name="Size">The input size in bytes.</param>
    /// <param name="Cores">How many threads ran at once.</param>
    /// <param name="CallsPerSecond">All threads together.</param>
    /// <param name="GigabytesPerSecond">All threads together.</param>
    /// <param name="NanosecondsPerCallPerCore">One call's time on its core.</param>
    /// <param name="Streams">Each thread's raw count and window.</param>
    private sealed record CellRecord(
        int Round,
        Implementation Implementation,
        int Size,
        int Cores,
        double CallsPerSecond,
        double GigabytesPerSecond,
        double NanosecondsPerCallPerCore,
        StreamResult[] Streams)
    {
        /// <summary>Copies a cell with its derived figures computed.</summary>
        /// <param name="cell">The timed cell.</param>
        /// <returns>The record to serialize.</returns>
        public static CellRecord From(CellResult cell) =>
            new(cell.Round, cell.Implementation, cell.Size, cell.Cores, cell.CallsPerSecond, cell.GigabytesPerSecond, cell.NanosecondsPerCallPerCore, cell.Streams);
    }

    /// <summary>The raw-counts file: the setup and every timed cell.</summary>
    /// <param name="Header">The run's setup.</param>
    /// <param name="Cells">Every timed cell, in the order it ran.</param>
    private sealed record RunDocument(Header Header, CellRecord[] Cells);

    /// <summary>What a cell's threads and its coordinator share.</summary>
    /// <param name="parties">The cell's threads plus the coordinator.</param>
    private sealed class CellControl(int parties)
    {
        /// <summary>Opens the window once every thread is pinned, checked and warmed up, and the coordinator is ready.</summary>
        public Barrier Start { get; } = new(parties);

        /// <summary>Raised to 1 by the coordinator when the window closes; threads read it between batches.</summary>
        public int Stop;
    }

    /// <summary>A cell could not be timed: a thread failed to pin itself or computed a wrong checksum.</summary>
    /// <param name="message">Which cell, and what went wrong.</param>
    /// <param name="inner">The thread's own exception.</param>
    private sealed class CellFailedException(string message, Exception inner) : Exception(message, inner);

    /// <summary>PROCESS_POWER_THROTTLING_STATE, version 1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        /// <summary>The structure's version: 1.</summary>
        public uint Version;

        /// <summary>Which knobs this call sets.</summary>
        public uint ControlMask;

        /// <summary>Each controlled knob's new state: 0 is "never throttle".</summary>
        public uint StateMask;
    }

    /// <summary>kernel32's SetThreadAffinityMask.</summary>
    /// <param name="thread">The thread; GetCurrentThread's pseudo-handle for the caller.</param>
    /// <param name="mask">The logical CPUs it may run on, as bits of processor group 0.</param>
    /// <returns>The previous mask, or 0 on failure (then GetLastPInvokeError).</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint SetThreadAffinityMask(nint thread, nuint mask);

    /// <summary>kernel32's GetCurrentThread: a pseudo-handle that means "the calling thread", never closed.</summary>
    /// <returns>The pseudo-handle.</returns>
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    /// <summary>kernel32's GetCurrentProcess: a pseudo-handle that means "this process", never closed.</summary>
    /// <returns>The pseudo-handle.</returns>
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>kernel32's SetProcessInformation.</summary>
    /// <param name="process">The process; GetCurrentProcess's pseudo-handle for this one.</param>
    /// <param name="informationClass">What to set: <see cref="ProcessPowerThrottling"/> here.</param>
    /// <param name="information">The setting.</param>
    /// <param name="size">Its size in bytes.</param>
    /// <returns>Nonzero on success; 0 on failure (then GetLastPInvokeError).</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetProcessInformation(nint process, int informationClass, PowerThrottlingState* information, uint size);

    /// <summary>The C library's sched_setaffinity (Linux).</summary>
    /// <param name="pid">0 for the calling thread.</param>
    /// <param name="size">The mask's size in bytes.</param>
    /// <param name="mask">The logical CPUs it may run on, one bit each.</param>
    /// <returns>0 on success; -1 on failure (then GetLastPInvokeError is errno).</returns>
    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nint size, byte* mask);
}
