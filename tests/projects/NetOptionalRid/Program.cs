using System.Runtime.InteropServices;

// Calls the optional asm library when this platform has it, and says so either way: the
// behavior OptionalOnUnlistedRuntimes asks of an app, which must cope with the library's
// absence on the RIDs the library does not build.
try
{
    Console.WriteLine($"optionalrid: {Native.Answer()}");
}
catch (DllNotFoundException)
{
    Console.WriteLine("optionalrid: absent");
}

/// <summary>The optional asm library's one function.</summary>
internal static partial class Native
{
    /// <summary>or_answer() of optionalrid: 42.</summary>
    /// <returns>42.</returns>
    /// <exception cref="DllNotFoundException">This platform's build has no optionalrid library.</exception>
    [LibraryImport("optionalrid", EntryPoint = "or_answer")]
    internal static partial int Answer();
}
