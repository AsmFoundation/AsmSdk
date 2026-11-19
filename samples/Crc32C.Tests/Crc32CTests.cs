using System.Runtime.Intrinsics.X86;
using System.Text;
using Xunit;

namespace Samples.Hashing.Tests;

/// <summary>
/// The CRC-32C library's tests: published check values, every path against a C# reference over
/// every length up to past several block sizes, chaining, unaligned data and the CPU dispatch.
/// </summary>
public sealed class Crc32CTests
{
    /// <summary>The paths this CPU can run (the theories below run once per path).</summary>
    public static TheoryData<Crc32CPath> Paths
    {
        get
        {
            var paths = new TheoryData<Crc32CPath>();
            foreach (Crc32CPath path in Enum.GetValues<Crc32CPath>().Where(Crc32C.IsSupported))
            {
                paths.Add(path);
            }

            return paths;
        }
    }

    /// <summary>
    /// The published check values: the CRC catalogue's "123456789" and RFC 3720 (iSCSI) appendix
    /// B.4's four 32-byte patterns.
    /// </summary>
    /// <param name="path">The path under test.</param>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Published_check_values(Crc32CPath path)
    {
        byte[] ascending = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(0xE3069283u, Crc32C.Append(0, "123456789"u8, path));
        Assert.Equal(0x8A9136AAu, Crc32C.Append(0, new byte[32], path));
        Assert.Equal(0x62A8AB43u, Crc32C.Append(0, Enumerable.Repeat((byte)0xFF, 32).ToArray(), path));
        Assert.Equal(0x46DD794Eu, Crc32C.Append(0, ascending, path));
        Assert.Equal(0x113FDB5Cu, Crc32C.Append(0, ascending.Reverse().ToArray(), path));
    }

    /// <summary>Nothing hashes to 0, and appending nothing leaves a checksum unchanged.</summary>
    /// <param name="path">The path under test.</param>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Empty_input_changes_nothing(Crc32CPath path)
    {
        Assert.Equal(0u, Crc32C.Append(0, [], path));
        Assert.Equal(0xE3069283u, Crc32C.Append(0xE3069283u, [], path));
    }

    /// <summary>
    /// Every length from 0 to 5,000 bytes against the reference: past the 192-byte and 1,536-byte
    /// blocks of the 3-way kernel, several of each, and every remainder after them.
    /// </summary>
    /// <param name="path">The path under test.</param>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Every_length_matches_the_reference(Crc32CPath path)
    {
        byte[] data = RandomBytes(5000, seed: 1);
        for (int length = 0; length <= data.Length; length++)
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            uint expected = Reference.Append(0, slice);
            uint actual = Crc32C.Append(0, slice, path);
            Assert.True(expected == actual, $"length {length}: 0x{actual:X8}, want 0x{expected:X8}");
        }
    }

    /// <summary>A megabyte and a block-and-a-byte off a megabyte, against the reference.</summary>
    /// <param name="path">The path under test.</param>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Large_inputs_match_the_reference(Crc32CPath path)
    {
        byte[] data = RandomBytes((1 << 20) + 1537, seed: 2);
        foreach (int length in new[] { 1 << 20, (1 << 20) + 1, (1 << 20) + 1537 })
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            Assert.Equal(Reference.Append(0, slice), Crc32C.Append(0, slice, path));
        }
    }

    /// <summary>Data that starts at every offset within 16 bytes: the kernels load unaligned quadwords.</summary>
    /// <param name="path">The path under test.</param>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Unaligned_data_matches_the_reference(Crc32CPath path)
    {
        byte[] data = RandomBytes(4096 + 16, seed: 3);
        for (int offset = 0; offset < 16; offset++)
        {
            ReadOnlySpan<byte> slice = data.AsSpan(offset, 4096);
            Assert.Equal(Reference.Append(0, slice), Crc32C.Append(0, slice, path));
        }
    }

    /// <summary>
    /// Appending the second part to the first part's checksum gives the whole input's checksum,
    /// whatever the split: the zlib convention the library promises.
    /// </summary>
    /// <param name="path">The path under test.</param>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Appending_continues_a_checksum(Crc32CPath path)
    {
        byte[] data = RandomBytes(4000, seed: 4);
        uint whole = Crc32C.Append(0, data, path);
        foreach (int split in new[] { 0, 1, 7, 8, 191, 192, 1535, 1536, 1537, 3999, 4000 })
        {
            uint first = Crc32C.Append(0, data.AsSpan(0, split), path);
            Assert.Equal(whole, Crc32C.Append(first, data.AsSpan(split), path));
        }
    }

    /// <summary>
    /// The dispatching entry agrees with the reference on both sides of its 4 KiB GC-transition
    /// boundary, and picks the 3-way path wherever the CPU has the CRC instruction and the
    /// carry-less multiply (SSE4.2 + PCLMULQDQ on x64, CRC32 + AES/PMULL on ARM64).
    /// </summary>
    [Fact]
    public void The_dispatcher_picks_the_fastest_path_and_agrees()
    {
        Crc32CPath expected = Crc32C.IsSupported(Crc32CPath.HardwareThreeWay) ? Crc32CPath.HardwareThreeWay
            : Crc32C.IsSupported(Crc32CPath.Hardware) ? Crc32CPath.Hardware : Crc32CPath.Portable;
        Assert.Equal(expected, Crc32C.Path);

        byte[] data = RandomBytes(8192, seed: 5);
        foreach (int length in new[] { 4095, 4096, 4097, 8192 })
        {
            ReadOnlySpan<byte> slice = data.AsSpan(0, length);
            Assert.Equal(Reference.Append(0, slice), Crc32C.HashToUInt32(slice));
        }

        Assert.Equal(0xE3069283u, Crc32C.HashToUInt32(Encoding.ASCII.GetBytes("123456789")));
    }

    /// <summary>Many threads checksum different data at once and each gets its own right answer.</summary>
    [Fact]
    public void Concurrent_calls_get_their_own_results()
    {
        byte[][] inputs = Enumerable.Range(0, 64).Select(i => RandomBytes(10_000 + i * 97, seed: 100 + i)).ToArray();
        uint[] expected = inputs.Select(input => Reference.Append(0, input)).ToArray();
        uint[] actual = new uint[inputs.Length];
        Parallel.For(0, inputs.Length * 50, n => actual[n % inputs.Length] = Crc32C.HashToUInt32(inputs[n % inputs.Length]));
        Assert.Equal(expected, actual);
    }

    /// <summary>A path that is not defined is refused before anything native runs.</summary>
    [Fact]
    public void An_undefined_path_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Crc32C.Append(0, [1, 2, 3], (Crc32CPath)42));

    /// <summary>Deterministic pseudo-random bytes, so a failure reproduces.</summary>
    /// <param name="count">How many bytes.</param>
    /// <param name="seed">The generator's seed.</param>
    /// <returns>The bytes.</returns>
    private static byte[] RandomBytes(int count, int seed)
    {
        byte[] bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// The reference: the bit-at-a-time definition of CRC-32C, written in C# and independent of the
    /// assembly code (no table, no crc32 instruction), so the two cannot share a mistake.
    /// </summary>
    private static class Reference
    {
        /// <summary>Continues a checksum one bit at a time.</summary>
        /// <param name="crc">The checksum to continue (0 for a new one).</param>
        /// <param name="data">The bytes.</param>
        /// <returns>The continued checksum.</returns>
        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            uint register = ~crc;
            foreach (byte b in data)
            {
                register ^= b;
                for (int bit = 0; bit < 8; bit++)
                {
                    // Reflected division step: shift right, fold the polynomial in when a 1 falls off.
                    register = (register >> 1) ^ (0x82F63B78u & (0u - (register & 1)));
                }
            }

            return ~register;
        }
    }
}
