using System.Runtime.InteropServices;

namespace Hello.Net;

/// <summary>
/// Calls into libhello.so (freestanding) and libhellolibc.so (on glibc), both built from
/// assembly by AsmFoundation.Sdk, and checks every answer.
/// </summary>
/// <remarks>
/// Exit code 0 means every check passed; 1 means at least one failed (each failure is printed).
/// The libraries are found next to the program: the ProjectReferences to the .asmproj projects
/// copy them there, and [LibraryImport("hello")] probes "libhello.so" on Linux.
/// </remarks>
internal static partial class Program
{
    /// <summary>int hello_add(int a, int b) from libhello.so.</summary>
    /// <param name="a">First addend.</param>
    /// <param name="b">Second addend.</param>
    /// <returns><paramref name="a"/> + <paramref name="b"/>, wrapping.</returns>
    [LibraryImport("hello", EntryPoint = "hello_add")]
    private static partial int HelloAdd(int a, int b);

    /// <summary>int hello_version(void) from libhello.so.</summary>
    /// <returns>The library version, major &lt;&lt; 16 | minor.</returns>
    [LibraryImport("hello", EntryPoint = "hello_version")]
    private static partial int HelloVersion();

    /// <summary>size_t hello_length(const char *text) from libhellolibc.so (calls libc's strlen).</summary>
    /// <param name="text">A NUL-terminated UTF-8 string.</param>
    /// <returns>Twice the string's length in bytes.</returns>
    [LibraryImport("hellolibc", EntryPoint = "hello_length", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint HelloLength(string text);

    /// <summary>Runs the checks and prints one line per check.</summary>
    /// <returns>0 when every check passed, otherwise 1.</returns>
    private static int Main()
    {
        int failures = 0;

        // Prints the check and counts a failure; returns nothing so the checks read as a list.
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
            if (!ok)
            {
                failures++;
            }
        }

        Check("hello_add(40, 2) == 42", HelloAdd(40, 2) == 42);
        Check("hello_version() == 0x00010000", HelloVersion() == 0x00010000);
        Check("hello_length(\"assembly\") == 16 (libc strlen through the PLT)", HelloLength("assembly") == 16);
        return failures == 0 ? 0 : 1;
    }
}
