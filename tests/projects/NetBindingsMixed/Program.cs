using AsmFoundation.Interop;

// bindpkg's baseline, reshaped by the code: every function in Bindpkg.Api. The generator reads the
// package's API file; the other libraries keep their default layout.
[assembly: AsmLibrary("bindpkg", DefaultNesting = AsmNesting.Flat)]

// The baselines, all written by the one generator now: a ProjectReference's and two packages'.
Console.WriteLine($"Apiproto.Add.AddInts {Apiproto.Add.AddInts(40, 2)}");
Console.WriteLine($"Bindpkg.Api.AddInts {Bindpkg.Api.AddInts(40, 2)}");
byte[] bytes = [1, 2, 3, 4];
Console.WriteLine($"Bindpkg.Api.SumBytes {Bindpkg.Api.SumBytes(bytes)}");
Console.WriteLine($"Bindpkgb.Mul.MulInts {Bindpkgb.Mul.MulInts(6, 7)}");
// A copy in the app's own class, beside the baseline (which keeps every function).
Console.WriteLine($"Native.MulInts {Native.MulInts(6, 7)}");
Console.WriteLine($"attribute {typeof(Native).Assembly.GetType("AsmFoundation.Interop.AsmLibraryAttribute") is not null}");

/// <summary>The app's copy of BindingsPackageB's functions, every one directly in the class.</summary>
[AsmLibrary("bindpkgb", Nesting = AsmNesting.Flat)]
internal static partial class Native
{
}
