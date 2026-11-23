using System.Runtime.InteropServices;

// WasmLibrary from its package (Test.WasmLibrary): the archive its props hand the app's build.
// ws_mul is WasmStatic's, a static library WasmLibrary links privately: the package holds no
// wasmstatic.a and depends on nothing, so it comes from wasmlib.a, which carries its member.
Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier} {Native.wl_add(40, 2)} {Native.wl_twice(21)} {Native.wl_platform()} {Native.wl_table_sum()} {Native.wl_simd(21)} {Native.ws_mul(6, 7)}");

/// <summary>The exports of the Test.WasmLibrary package (tests/projects/WasmLibrary).</summary>
internal static partial class Native
{
    /// <summary>a + b.</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_add(int a, int b);

    /// <summary>2x, through a hidden helper and TAILCALL.</summary>
    /// <param name="x">The value.</param>
    /// <returns>Twice the value.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_twice(int x);

    /// <summary>The SDK's defines as a sum: 1 BROWSER, 2 WASI, 10 RELEASE, 100 FREESTANDING.</summary>
    /// <returns>111 for the Release browser-wasm build in the package.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_platform();

    /// <summary>The sum of the library's table in read-only data.</summary>
    /// <returns>42.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_table_sum();

    /// <summary>x + x through SIMD128 lanes.</summary>
    /// <param name="x">The value.</param>
    /// <returns>Twice the value.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_simd(int x);

    /// <summary>a * b: the static library's function, inside the package's archive.</summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second factor.</param>
    /// <returns>The product.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int ws_mul(int a, int b);
}
