// NumSharp arrays, computed on by asm: np.arange makes two float64 arrays, and the asm kernels
// read their memory in place (no copy) for a sum and a dot product, which must equal NumSharp's
// own np.sum and np.dot. The values are integers below 2^53, so every order of summation gives
// the exact same double, whatever NumSharp's kernels do. Exit status 0 when both agree.
using System.Runtime.InteropServices;
using NumSharp;

NDArray a = np.arange(1.0, 101.0, 1.0);    // 1, 2, ..., 100 (float64)
NDArray b = a * 2.0;                        // 2, 4, ..., 200

double npSum = np.sum(a).GetDouble();
double asmSum = Kernels.Sum(a);
Console.WriteLine($"np.sum = {npSum}, asm sum = {asmSum}");

double npDot = np.dot(a, b).GetDouble();
double asmDot = Kernels.Dot(a, b);
Console.WriteLine($"np.dot = {npDot}, asm dot = {asmDot}");

bool agree = npSum == asmSum && npDot == asmDot;
Console.WriteLine(agree ? "NumSharp and the asm kernels agree" : "NumSharp and the asm kernels DISAGREE");
return agree ? 0 : 1;

/// <summary>
/// The asm kernels on NDArray memory. NumSharp keeps an array's data in unmanaged memory, so
/// its address (NDArray.Unsafe.Address, public; NDArray.Address is internal) can go to native
/// code without pinning; the array must only stay alive (and contiguous, float64) for the call,
/// which these wrappers check and ensure.
/// </summary>
internal static unsafe partial class Kernels
{
    /// <summary>The sum of a contiguous float64 array, computed in asm on its memory.</summary>
    /// <param name="values">The array: one-dimensional, float64, contiguous.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="ArgumentException">The array is not contiguous float64 memory.</exception>
    internal static double Sum(NDArray values)
    {
        CheckLayout(values);
        double sum = SumCore((double*)(void*)values.Unsafe.Address, (nuint)values.size);
        // The finalizer of an unreachable NDArray frees its memory: without this, the GC could
        // collect it while the asm code still reads the pointer.
        GC.KeepAlive(values);
        return sum;
    }

    /// <summary>The dot product of two contiguous float64 arrays of one length, computed in asm.</summary>
    /// <param name="a">The first array.</param>
    /// <param name="b">The second array, as long as the first.</param>
    /// <returns>The dot product.</returns>
    /// <exception cref="ArgumentException">An array is not contiguous float64 memory, or the lengths differ.</exception>
    internal static double Dot(NDArray a, NDArray b)
    {
        CheckLayout(a);
        CheckLayout(b);
        if (a.size != b.size)
        {
            throw new ArgumentException($"lengths differ: {a.size} and {b.size}", nameof(b));
        }

        double dot = DotCore((double*)(void*)a.Unsafe.Address, (double*)(void*)b.Unsafe.Address, (nuint)a.size);
        GC.KeepAlive(a);    // see Sum
        GC.KeepAlive(b);
        return dot;
    }

    /// <summary>Refuses an array the kernels cannot read as one run of doubles (a strided view, another dtype).</summary>
    /// <param name="array">The array.</param>
    /// <exception cref="ArgumentException">It is not contiguous, or not float64.</exception>
    private static void CheckLayout(NDArray array)
    {
        if (array.dtype != typeof(double) || !array.Shape.IsContiguous)
        {
            throw new ArgumentException("the asm kernels read contiguous float64 memory only", nameof(array));
        }
    }

    /// <summary>kernels_sum: reads <paramref name="count"/> doubles from <paramref name="values"/>.</summary>
    /// <param name="values">The first value.</param>
    /// <param name="count">How many values.</param>
    /// <returns>The sum.</returns>
    [LibraryImport("kernels", EntryPoint = "kernels_sum")]
    private static partial double SumCore(double* values, nuint count);

    /// <summary>kernels_dot: reads <paramref name="count"/> doubles from each array.</summary>
    /// <param name="a">The first array's first value.</param>
    /// <param name="b">The second array's first value.</param>
    /// <param name="count">How many values each holds.</param>
    /// <returns>The dot product.</returns>
    [LibraryImport("kernels", EntryPoint = "kernels_dot")]
    private static partial double DotCore(double* a, double* b, nuint count);
}
