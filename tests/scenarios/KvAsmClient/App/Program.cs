// Stores 100 values of 1 to 1,000 bytes in a KvAsm server (the address is the one argument),
// each with its FNV-1a computed by the asm library and checked against C#, then reads every
// value back and checks it by the same checksum. Exit status 0 when all 100 keys were new and
// came back intact, 1 otherwise, 2 for a wrong command line.
using System.Runtime.InteropServices;
using System.Text;
using KvAsm.Client;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: App <http://host:port of a KvAsm server>");
    return 2;
}

const int Count = 100;
using var kv = new KvAsmClient(new Uri(args[0]));
var checksums = new ulong[Count];
int created = 0;
for (int i = 0; i < Count; i++)
{
    byte[] value = Value(i);
    checksums[i] = Checksum.Fnv1a(value);
    if (checksums[i] != Fnv1aInCSharp(value))
    {
        Console.WriteLine($"the asm FNV-1a of value {i} differs from C#'s");
        return 1;
    }

    created += await kv.PutAsync(Key(i), value) ? 1 : 0;
}

for (int i = 0; i < Count; i++)
{
    ReadOnlyMemory<byte>? stored = await kv.GetAsync(Key(i));
    if (stored is not { } bytes || Checksum.Fnv1a(bytes.Span) != checksums[i])
    {
        Console.WriteLine($"key {i} came back {(stored is null ? "missing" : "changed")}");
        return 1;
    }
}

Console.WriteLine($"stored {created} new keys in KvAsm and read all {Count} back: FNV-1a (asm) matches");
return created == Count ? 0 : 1;

// The key of value i.
static byte[] Key(int i) => Encoding.UTF8.GetBytes($"scenario:{i}");

// Value i: 1 to 1,000 bytes of a pattern that differs per value and per position.
static byte[] Value(int i)
{
    var value = new byte[1 + i * 37 % 1000];
    for (int j = 0; j < value.Length; j++)
    {
        value[j] = (byte)(i * 31 + j * 7);
    }

    return value;
}

// FNV-1a in C#: the reference the asm code must match.
static ulong Fnv1aInCSharp(ReadOnlySpan<byte> data)
{
    ulong hash = 0xcbf29ce484222325;
    foreach (byte b in data)
    {
        hash = (hash ^ b) * 0x100000001b3;
    }

    return hash;
}

/// <summary>The export of the asm library checksum.</summary>
internal static unsafe partial class Checksum
{
    /// <summary>FNV-1a (64-bit) of the bytes, computed in asm on them in place.</summary>
    /// <param name="data">The bytes; pinned for the call.</param>
    /// <returns>The hash.</returns>
    internal static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        fixed (byte* first = data)
        {
            return Fnv1aCore(first, (nuint)data.Length);
        }
    }

    /// <summary>checksum_fnv1a: reads <paramref name="length"/> bytes from <paramref name="data"/>.</summary>
    /// <param name="data">The first byte; must stay pinned during the call.</param>
    /// <param name="length">How many bytes.</param>
    /// <returns>The hash.</returns>
    [LibraryImport("checksum", EntryPoint = "checksum_fnv1a")]
    private static partial ulong Fnv1aCore(byte* data, nuint length);
}
