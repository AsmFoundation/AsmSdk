using System.Runtime.InteropServices;

// Every struct below is in ApiStruct's baseline (the namespace Apistruct, written by the SDK from
// its STRUCT lines), and every call goes through a binding of it. The asm code reads (and
// point_set writes) each field at the offset the SDK
// gave it; the numbers come out right only where C# lays each field at the same offset.
unsafe
{
    // A struct holding two structs by value: rect.max + point.y is offset 12.
    Apistruct.Rect rect = default;
    rect.Min.X = 1;
    rect.Min.Y = 2;
    rect.Max.X = 4;
    rect.Max.Y = 7;
    Console.WriteLine($"rect_area {Apistruct.Shapes.RectArea(&rect)}");

    // Written by the asm code, read here.
    Apistruct.Point point = default;
    Apistruct.Shapes.PointSet(&point, 40, 2);
    Console.WriteLine($"point_set {point.X} {point.Y}");

    // Fixed buffers and a bool: 1 + ... + 8 + 100 + 7 + 1.
    Apistruct.HashState hash = default;
    for (int i = 0; i < 8; i++)
    {
        hash.H[i] = (uint)(i + 1);
    }

    hash.Length = 100;
    hash.Block[63] = 7;
    hash.Final = true;
    Console.WriteLine($"state_sum {Apistruct.Kinds.StateSum(&hash)}");

    // A function pointer field, called by the asm code: 40 + 2.
    Apistruct.Sorter sorter = default;
    sorter.Cmp = &Callbacks.Add;
    sorter.Next = &sorter;
    Console.WriteLine($"sorter_call {Apistruct.Kinds.SorterCall(&sorter, 40, 2)}");

    // A bool, a pointer after padding, a char: 30 + 6 + 5 + 1.
    byte* data = stackalloc byte[] { 5 };
    Apistruct.Mixed mixed = default;
    mixed.Flag = true;
    mixed.Data = data;
    mixed.Count = 30;
    mixed.Tail = 6;
    mixed.Tag = 1;
    Console.WriteLine($"mixed_get {Apistruct.Kinds.MixedGet(&mixed)}");

    // The asm code's size constants next to C#'s sizeof, struct by struct.
    nuint* sizes = stackalloc nuint[5];
    nuint count = Apistruct.Kinds.StructSizes(sizes);
    int[] csharp = [sizeof(Apistruct.Point), sizeof(Apistruct.Rect), sizeof(Apistruct.HashState), sizeof(Apistruct.Sorter), sizeof(Apistruct.Mixed)];
    Console.WriteLine("sizes " + string.Join(" ", Enumerable.Range(0, (int)count).Select(i => $"{sizes[i]}={csharp[i]}")));
}

/// <summary>The callback the asm code calls through sorter.cmp.</summary>
internal static class Callbacks
{
    /// <summary>a + b, callable from native code.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>a + b.</returns>
    [UnmanagedCallersOnly]
    internal static int Add(int a, int b) => a + b;
}
