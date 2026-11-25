using System.Reflection;
using AsmFoundation.Interop;
using NetBindingsOptions;

// The copies' default for every library: no span overloads (Vector turns them back on).
[assembly: AsmLibrary(SpanOverloads = false)]
// ApiProto's baseline, reshaped: the namespace Proto, a class per folder (the root files' functions
// in Proto.Api), instantiable, virtual, protected internal (so Proto.Api is a public class).
[assembly: AsmLibrary("apiproto", DefaultNamespace = "Proto", DefaultNesting = AsmNesting.Folders, DefaultClassKind = AsmClassKind.Instance,
    DefaultVirtual = true, DefaultVisibility = AsmVisibility.ProtectedInternal)]

// The baseline keeps every function, the copies' too.
var api = new Proto.Api();
Console.WriteLine($"Proto.Api.AddInts {api.AddInts(40, 2)}");
MethodInfo addInts = typeof(Proto.Api).GetMethod("AddInts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
Console.WriteLine($"Proto.Api virtual {addInts.IsVirtual} protected internal {addInts.IsFamilyOrAssembly} public class {typeof(Proto.Api).IsPublic}");
Proto.Api overridden = new FixedApi();
Console.WriteLine($"Proto.Api overridden {overridden.AddInts(40, 2)}");
byte[] bytes = [5, 5];
Console.WriteLine($"Proto.Simd.SumBytes {new Proto.Simd().SumBytes(bytes)}");

// The copies.
Console.WriteLine($"Text.NameLength {Text.NameLength("abc")} public {typeof(Text).GetMethod("NameLength")!.IsPublic}");
Console.WriteLine($"Vector.SumBytes {Vector.SumBytes(bytes)} overloads {Overloads(typeof(Vector), "SumBytes")}");
Console.WriteLine($"NoSpans overloads {Overloads(typeof(NoSpans), "SumBytes")}");
Console.WriteLine($"MathA.AddInts {MathA.AddInts(40, 2)} MathB.AddInts {MathB.AddInts(1, 1)}");
Calculator calculator = new FixedCalculator();
Console.WriteLine($"Calculator {new Calculator().AddInts(40, 2)} overridden {calculator.AddInts(40, 2)}");

// The generator ran (this project mentions the attribute), so its type is in the assembly.
Console.WriteLine($"attribute {typeof(Text).Assembly.GetType("AsmFoundation.Interop.AsmLibraryAttribute") is not null}");

// The methods of a name a class declares, static or instance (the extern helpers have other names).
static int Overloads(Type type, string name) =>
    type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Count(m => m.Name == name);
