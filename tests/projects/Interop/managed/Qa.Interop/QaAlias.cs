using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AsmQa.Interop;

/// <summary>
/// A custom import resolver: P/Invoke declarations that name the logical library "qa-alias" get
/// qamath, through <see cref="NativeLibrary.SetDllImportResolver"/>. This is how a library maps a
/// stable name to a platform-specific file of its own choosing. Footgun: one resolver per assembly,
/// registered before the first call that needs it (here, by a module initializer); it sees every
/// P/Invoke of the assembly, so it must return 0 for the names it does not handle.
/// </summary>
public static partial class QaAlias
{
    /// <summary>qa_add declared against the alias with source-generated P/Invoke.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="DllNotFoundException">The resolver found no qamath for this platform.</exception>
    [LibraryImport("qa-alias", EntryPoint = "qa_add")]
    public static partial int Add(int a, int b);

    /// <summary>qa_is_even declared against the alias with classic P/Invoke.</summary>
    /// <param name="value">The number.</param>
    /// <returns>True when even.</returns>
    /// <exception cref="DllNotFoundException">The resolver found no qamath for this platform.</exception>
    [DllImport("qa-alias", EntryPoint = "qa_is_even")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsEven(long value);

    /// <summary>How many times the resolver mapped the alias: proof that it, not the default probing, ran.</summary>
    public static int Resolutions => resolutions;

    /// <summary>Counts <see cref="Resolve"/>'s mappings.</summary>
    private static int resolutions;

    /// <summary>Registers the resolver when the assembly loads, before any P/Invoke of it runs.</summary>
    [ModuleInitializer]
    internal static void Register() => NativeLibrary.SetDllImportResolver(typeof(QaAlias).Assembly, Resolve);

    /// <summary>Maps "qa-alias" to qamath; every other name gets the default probing (0).</summary>
    /// <param name="name">The library name of the declaration.</param>
    /// <param name="assembly">The declaring assembly.</param>
    /// <param name="searchPath">The declaration's search path, if any.</param>
    /// <returns>The library handle, or 0 to fall back to the default probing.</returns>
    /// <exception cref="DllNotFoundException">qamath cannot be loaded on this platform.</exception>
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "qa-alias")
        {
            return 0;
        }

        Interlocked.Increment(ref resolutions);
        return NativeLibrary.Load("qamath", assembly, searchPath);
    }
}
