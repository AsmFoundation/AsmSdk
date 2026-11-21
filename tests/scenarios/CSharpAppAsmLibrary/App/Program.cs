// Calls the asm library calc: calc_add with the two numbers of the command line, and calc_sum
// over 1..100. Each result is printed and compared with C#'s; the exit status is 0 when both
// agree, 1 when one does not, 2 for a wrong command line.
using System.Globalization;
using System.Runtime.InteropServices;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: App <a> <b>");
    return 2;
}

int a = int.Parse(args[0], CultureInfo.InvariantCulture);
int b = int.Parse(args[1], CultureInfo.InvariantCulture);
int added = Calc.Add(a, b);
Console.WriteLine($"calc_add({a}, {b}) = {added}");

int[] values = Enumerable.Range(1, 100).ToArray();
long sum = Calc.Sum(values);
Console.WriteLine($"calc_sum(1..100) = {sum}");
return added == a + b && sum == values.Sum(value => (long)value) ? 0 : 1;

/// <summary>
/// The exports of the asm library calc. "calc" finds calc.dll, libcalc.so or libcalc.dylib by
/// .NET's own naming rules: from deps.json's runtimes/ entries in a portable build, from the
/// app's folder in a RID-specific one.
/// </summary>
internal static unsafe partial class Calc
{
    /// <summary>a + b, computed in asm.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum (wraps on overflow, like C#'s unchecked addition).</returns>
    [LibraryImport("calc", EntryPoint = "calc_add")]
    internal static partial int Add(int a, int b);

    /// <summary>The sum of the values, widened to 64 bits, computed in asm.</summary>
    /// <param name="values">The values; pinned for the call, so the asm code reads them in place.</param>
    /// <returns>The sum.</returns>
    internal static long Sum(ReadOnlySpan<int> values)
    {
        fixed (int* first = values)
        {
            return SumCore(first, (nuint)values.Length);
        }
    }

    /// <summary>calc_sum itself: the asm code reads <paramref name="count"/> values from <paramref name="values"/>.</summary>
    /// <param name="values">The first value; must stay pinned during the call.</param>
    /// <param name="count">How many values.</param>
    /// <returns>The sum.</returns>
    [LibraryImport("calc", EntryPoint = "calc_sum")]
    private static partial long SumCore(int* values, nuint count);
}
