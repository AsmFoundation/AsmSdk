using System.Reflection;

// What the generator would have written, looked up in this program's own assembly by name: a
// compile-time reference would not build once the fix holds, and reflection reports both ways.
// The default class would be Apiprogram (the program's AssemblyName in PascalCase) in this
// project's RootNamespace; the generator, whenever it runs, adds its [AsmLibrary] attribute.
// Program is the class the compiler generates for these top-level statements.
Assembly self = typeof(Program).Assembly;
Console.WriteLine($"program class {self.GetType("NetApiProgram.Apiprogram") is not null}");
Console.WriteLine($"generator {self.GetType("AsmFoundation.Interop.AsmLibraryAttribute") is not null}");
