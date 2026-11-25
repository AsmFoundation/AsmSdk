using System.Reflection;

// What the bindings would have been, looked up in this program's own assembly: ApiProgram's
// baseline would be the namespace Apiprogram (the program's AssemblyName in PascalCase), with a
// class per .asm file (Apiprogram.Main). Reflection reports both ways; a compile-time reference
// would not build once the rule holds. Program is the class the compiler generates for these
// top-level statements.
Assembly self = typeof(Program).Assembly;
Console.WriteLine($"program class {self.GetTypes().Any(t => t.Namespace == "Apiprogram")}");
Console.WriteLine($"generator {self.GetType("AsmFoundation.Interop.AsmLibraryAttribute") is not null}");
