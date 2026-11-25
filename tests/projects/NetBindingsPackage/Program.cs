// Every call goes through the baseline of the package Test.BindingsPackage: the namespace Bindpkg
// (the library's name), a class per .asm file, folders as namespaces, the struct at the root,
// all internal and static (the defaults).
Console.WriteLine($"add_ints {Bindpkg.Add.AddInts(40, 2)}");
// The span overload of sum_bytes(const uint8_t* data, size_t len): the array pinned, no copy.
byte[] bytes = [1, 2, 3, 4];
Console.WriteLine($"sum_bytes {Bindpkg.Simd.Sum.SumBytes(bytes)}");
unsafe
{
    // point_sum(const point* p) reads the fields at the offsets the SDK gave the asm code.
    var point = new Bindpkg.Point { X = 40, Y = 2 };
    Console.WriteLine($"point_sum {Bindpkg.Geometry.PointSum(&point)}");
}

Console.WriteLine($"internal {!typeof(Bindpkg.Add).IsPublic}");
// No generator ran: its attribute type is not in the assembly. The type's name is split in two,
// since the switch is a plain word search of the sources and the whole name would turn it on.
Console.WriteLine($"attribute {typeof(Bindpkg.Add).Assembly.GetType("AsmFoundation.Interop.Asm" + "LibraryAttribute") is not null}");
