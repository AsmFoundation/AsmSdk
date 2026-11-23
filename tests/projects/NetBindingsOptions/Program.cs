using System.Reflection;
using AsmFoundation.Interop;
using NetBindingsOptions;

[assembly: AsmLibrary(SpanOverloads = false)]
[assembly: AsmLibrary("apiproto", ClassName = "Proto", Nesting = AsmNesting.Folders)]

// The default class Proto keeps what no class took (add_ints too: Alias only copies it), laid out
// by folder, so the root folder's functions sit directly in it.
Console.WriteLine($"Proto.AddInts {Proto.AddInts(40, 2)}");
Console.WriteLine($"Proto.Answer {Proto.Answer()}");
Console.WriteLine($"Alias.AddInts {Alias.AddInts(1, 1)}");
Console.WriteLine($"Text.NameLength {Text.NameLength("abc")}");
byte[] bytes = [5, 5];
Console.WriteLine($"Vector.SumBytes {Vector.SumBytes(bytes)}");
Console.WriteLine($"Text public {typeof(Text).GetMethod("NameLength")!.IsPublic}");
Console.WriteLine($"Vector overloads {Overloads(typeof(Vector), "SumBytes")}");
Console.WriteLine($"NoSpans overloads {Overloads(typeof(NoSpans), "SumBytes")}");

// The methods of a name a class declares (the LibraryImport stubs' helpers have other names).
static int Overloads(Type type, string name) =>
    type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Count(m => m.Name == name);
