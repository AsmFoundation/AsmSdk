using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Bits.Tests;

/// <summary>
/// The asm library bits against .NET's own bit operations: the edge values every bit routine
/// gets wrong first (0, one bit at each end, all ones, alternating patterns), every rotation
/// count including the ones that wrap, and 10,000 seeded random values.
/// </summary>
public sealed class BitsTests
{
    /// <summary>The edge values: no bit, the lowest, the highest, all, and alternating patterns.</summary>
    public static TheoryData<ulong> Edges =>
    [
        0UL, 1UL, 2UL, 3UL, 0x8000_0000_0000_0000UL, 0x7FFF_FFFF_FFFF_FFFFUL, ulong.MaxValue,
        0x5555_5555_5555_5555UL, 0xAAAA_AAAA_AAAA_AAAAUL, 0x0123_4567_89AB_CDEFUL, 0x0000_0001_0000_0000UL,
    ];

    /// <summary>bits_popcount counts the set bits as BitOperations.PopCount does.</summary>
    /// <param name="value">The value.</param>
    [Theory]
    [MemberData(nameof(Edges))]
    public void Popcount_matches_dotnet(ulong value) =>
        Assert.Equal(BitOperations.PopCount(value), Native.Popcount(value));

    /// <summary>bits_leading_zeros counts as BitOperations.LeadingZeroCount does, 64 for zero.</summary>
    /// <param name="value">The value.</param>
    [Theory]
    [MemberData(nameof(Edges))]
    public void Leading_zeros_match_dotnet(ulong value) =>
        Assert.Equal(BitOperations.LeadingZeroCount(value), Native.LeadingZeros(value));

    /// <summary>
    /// bits_rotate_left rotates as BitOperations.RotateLeft does, for counts that wrap (64, 65)
    /// and negative ones (a rotation the other way).
    /// </summary>
    /// <param name="count">The rotation count.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(-1)]
    [InlineData(-64)]
    public void Rotate_left_matches_dotnet(int count)
    {
        const ulong value = 0x0123_4567_89AB_CDEFUL;
        Assert.Equal(BitOperations.RotateLeft(value, count), Native.RotateLeft(value, count));
    }

    /// <summary>All three operations agree with .NET on 10,000 random values (a fixed seed, so a failure repeats).</summary>
    [Fact]
    public void Random_values_match_dotnet()
    {
        var random = new Random(20261121);
        for (int i = 0; i < 10_000; i++)
        {
            // NextInt64 never sets bit 63: shifting and filling bit 0 covers all 64 bits.
            ulong value = ((ulong)random.NextInt64() << 1) ^ (ulong)random.Next(2);
            int count = random.Next(-128, 128);
            Assert.Equal(BitOperations.PopCount(value), Native.Popcount(value));
            Assert.Equal(BitOperations.LeadingZeroCount(value), Native.LeadingZeros(value));
            Assert.Equal(BitOperations.RotateLeft(value, count), Native.RotateLeft(value, count));
        }
    }
}

/// <summary>
/// The exports of the asm library bits: "bits" finds bits.dll, libbits.so or libbits.dylib by
/// .NET's naming rules, from the test output's runtimes/ folder for this machine's RID.
/// </summary>
internal static partial class Native
{
    /// <summary>bits_popcount: the number of set bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>0 to 64.</returns>
    [LibraryImport("bits", EntryPoint = "bits_popcount")]
    internal static partial int Popcount(ulong value);

    /// <summary>bits_leading_zeros: the number of zero bits above the highest set bit.</summary>
    /// <param name="value">The value.</param>
    /// <returns>0 to 64 (64 for zero).</returns>
    [LibraryImport("bits", EntryPoint = "bits_leading_zeros")]
    internal static partial int LeadingZeros(ulong value);

    /// <summary>bits_rotate_left: the value rotated left by the count modulo 64.</summary>
    /// <param name="value">The value.</param>
    /// <param name="count">The count; negative rotates right.</param>
    /// <returns>The rotated value.</returns>
    [LibraryImport("bits", EntryPoint = "bits_rotate_left")]
    internal static partial ulong RotateLeft(ulong value, int count);
}
