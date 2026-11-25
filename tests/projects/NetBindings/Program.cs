using System.Globalization;
using System.Runtime.InteropServices;

// Every call below goes through ApiProto's baseline: the namespace Apiproto (the library's
// name), a class per .asm file, written by the SDK from ApiProto's PROTO lines (this project
// never mentions the attribute, so no generator runs).
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

// No generator ran (this project never mentions the attribute), so its type is not in the assembly.
// The type's name is split in two: the switch is a plain word search of the sources, and the
// whole name in one string would turn the generator on (measured: "attribute True").
Console.WriteLine($"attribute {typeof(Callbacks).Assembly.GetType("AsmFoundation.Interop.Asm" + "LibraryAttribute") is not null}");

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
