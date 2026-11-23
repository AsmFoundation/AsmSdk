using System.Globalization;
using System.Runtime.InteropServices;
using NetBindings;

// Every call below goes through a binding the generator wrote from ApiProto's PROTO lines.
Console.WriteLine($"add_ints {Apiproto.Add.AddInts(40, 2)}");
Console.WriteLine($"answer {Apiproto.Answer.Invoke()}");
Console.WriteLine($"halve {Apiproto.Halve.Invoke(5.0).ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine($"name_length {Apiproto.Strings.NameLength("hello")}");
// The span overload of sum_bytes(const uint8_t* data, size_t len): the array pinned, no copy.
byte[] bytes = [1, 2, 3, 4];
Console.WriteLine($"sum_bytes {Apiproto.Simd.Sum.SumBytes(bytes)}");
unsafe
{
    // apply's int32_t (*op)(int32_t, int32_t) is a delegate* unmanaged<int, int, int>.
    Console.WriteLine($"apply {Apiproto.Callback.Apply(&Callbacks.Add, 40, 2)}");
}

/// <summary>The callback handed to the asm function apply.</summary>
internal static class Callbacks
{
    /// <summary>a + b, callable from native code.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>a + b.</returns>
    [UnmanagedCallersOnly]
    internal static int Add(int a, int b) => a + b;
}
