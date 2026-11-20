namespace AsmQa.App;

/// <summary>
/// The interop tour's harness: runs named checks, prints one PASS/FAIL line each, and counts failures. A check
/// that throws counts as failed with the exception's type and message, so one broken technique
/// never hides the others.
/// </summary>
internal sealed class Checks
{
    /// <summary>How many checks passed.</summary>
    public int Passed { get; private set; }

    /// <summary>How many checks failed (the process's exit code).</summary>
    public int Failed { get; private set; }

    /// <summary>Runs one check.</summary>
    /// <param name="name">What it checks, prefixed with the technique.</param>
    /// <param name="body">Returns null when the check holds, else what was wrong.</param>
    public void Run(string name, Func<string?> body)
    {
        string? error;
        try
        {
            error = body();
        }
        catch (Exception exception)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
        }

        if (error is null)
        {
            Passed++;
            Console.WriteLine($"PASS  {name}");
        }
        else
        {
            Failed++;
            Console.WriteLine($"FAIL  {name}: {error}");
        }
    }

    /// <summary>A check's verdict from a comparison.</summary>
    /// <typeparam name="T">The compared type.</typeparam>
    /// <param name="expected">The C# reference's value.</param>
    /// <param name="actual">The native value.</param>
    /// <returns>null when equal, else both values.</returns>
    public static string? Same<T>(T expected, T actual) =>
        EqualityComparer<T>.Default.Equals(expected, actual) ? null : $"expected {expected}, got {actual}";
}
