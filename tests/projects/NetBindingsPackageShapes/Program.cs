using System.Reflection;

// The baseline as this project file shaped it: instance classes, public and sealed, a class per
// folder in the namespace Shapes.Pkg, no span overloads.
var api = new Shapes.Pkg.Api();
unsafe
{
    var point = new Shapes.Pkg.Point { X = 40, Y = 2 };
    Console.WriteLine($"Api.AddInts {api.AddInts(40, 2)} Api.PointSum {api.PointSum(&point)}");
    byte[] bytes = [1, 2, 3, 4];
    fixed (byte* data = bytes)
    {
        Console.WriteLine($"Simd.SumBytes {new Shapes.Pkg.Simd().SumBytes(data, (nuint)bytes.Length)} overloads {Overloads(typeof(Shapes.Pkg.Simd), "SumBytes")}");
    }
}

MethodInfo addInts = typeof(Shapes.Pkg.Api).GetMethod("AddInts")!;
// A static class is abstract and sealed in IL; an instance class that is sealed is not abstract.
Console.WriteLine($"public {typeof(Shapes.Pkg.Api).IsPublic} sealed {typeof(Shapes.Pkg.Api).IsSealed} static {typeof(Shapes.Pkg.Api).IsAbstract} instance {!addInts.IsStatic} virtual {addInts.IsVirtual}");
Console.WriteLine($"Point public {typeof(Shapes.Pkg.Point).IsPublic}");

// The methods of a name a class declares (the extern helpers have other names).
static int Overloads(Type type, string name) =>
    type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Count(m => m.Name == name);
