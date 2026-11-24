// coreguard: two helpers for timing on a busy hybrid CPU (Windows; measured on an i9-13900K).
//   topology        prints each physical core's logical CPUs and efficiency class (P-cores have
//                   the higher class), to find a P-core's two hyperthreads
//   spin <seconds>  holds one logical CPU with pause instructions (Thread.SpinWait) until the time
//                   is up. Run pinned to the timed CPU's hyperthread sibling at High priority, it
//                   keeps other processes' threads off that core while taking almost none of its
//                   execution resources (pause yields them to the sibling)
// Build once: dotnet build coreguard.cs -c Release -o <folder>  (then <folder>/coreguard.exe)
#:property AllowUnsafeBlocks=true
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length >= 1 && args[0] == "topology")
{
    Topology.Print();
    return 0;
}

if (args.Length == 2 && args[0] == "spin" && double.TryParse(args[1], out double seconds))
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        Thread.SpinWait(1000);
    }

    return 0;
}

Console.Error.WriteLine("usage: coreguard topology | coreguard spin <seconds>");
return 2;

/// <summary>Reads the processor topology through GetLogicalProcessorInformationEx(RelationProcessorCore).</summary>
internal static unsafe class Topology
{
    /// <summary>RelationProcessorCore: one record per physical core.</summary>
    private const int RelationProcessorCore = 0;

    /// <summary>Prints one line per physical core: its logical CPUs (group 0 mask bits) and efficiency class.</summary>
    /// <exception cref="InvalidOperationException">The Win32 call failed.</exception>
    internal static void Print()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
        byte[] buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, start, ref length))
            {
                throw new InvalidOperationException($"GetLogicalProcessorInformationEx failed: {Marshal.GetLastWin32Error()}");
            }

            // Each record: Relationship (4), Size (4), then PROCESSOR_RELATIONSHIP: Flags (1),
            // EfficiencyClass (1), Reserved (20), GroupCount (2), then GROUP_AFFINITY entries
            // (Mask as a pointer-sized integer, Group (2), Reserved (6)).
            int offset = 0, core = 0;
            while (offset < length)
            {
                byte* record = start + offset;
                int size = *(int*)(record + 4);
                byte efficiency = record[8 + 1];
                ulong mask = *(ulong*)(record + 8 + 1 + 1 + 20 + 2);
                var cpus = Enumerable.Range(0, 64).Where(bit => (mask & (1UL << bit)) != 0);
                Console.WriteLine($"core {core,2}: logical CPUs {string.Join(",", cpus),-6} efficiency class {efficiency}");
                offset += size;
                core++;
            }
        }
    }

    /// <summary>kernel32's GetLogicalProcessorInformationEx.</summary>
    /// <param name="relationship">The relation to report.</param>
    /// <param name="buffer">The output buffer, or null to ask for its size.</param>
    /// <param name="length">In: the buffer's size; out: the size needed or written.</param>
    /// <returns>True on success.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);
}
