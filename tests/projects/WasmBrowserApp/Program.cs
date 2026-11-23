using System.Runtime.InteropServices;

// Hand-written WebAssembly, linked into the .NET runtime of a browser-wasm app and called through
// P/Invoke: each library is the module its project's AssemblyName names.
Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier} {Native.wl_add(40, 2)} {Native.wl_twice(21)} {Native.wl_platform()} {Native.wl_table_sum()} {Native.wl_simd(21)} {Native.ws_mul(6, 7)} {Native.wo_sub(50, 8)}");

/// <summary>The exports of WasmLibrary, WasmStatic and WasmObjects (tests/projects).</summary>
internal static partial class Native
{
    /// <summary>a + b (WasmLibrary).</summary>
    /// <param name="a">The first addend.</param>
    /// <param name="b">The second addend.</param>
    /// <returns>The sum.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_add(int a, int b);

    /// <summary>2x, through a hidden helper and a tail call (WasmLibrary).</summary>
    /// <param name="x">The value.</param>
    /// <returns>Twice the value.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_twice(int x);

    /// <summary>The SDK's defines as a sum: 1 BROWSER, 2 WASI, 10 RELEASE, 100 FREESTANDING (WasmLibrary).</summary>
    /// <returns>111 for a Release browser-wasm build.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_platform();

    /// <summary>The sum of the library's table in read-only data (WasmLibrary).</summary>
    /// <returns>42.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_table_sum();

    /// <summary>x + x through SIMD128 lanes (WasmLibrary).</summary>
    /// <param name="x">The value.</param>
    /// <returns>Twice the value.</returns>
    [LibraryImport("wasmlib")]
    internal static partial int wl_simd(int x);

    /// <summary>a * b (WasmStatic, a static library).</summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second factor.</param>
    /// <returns>The product.</returns>
    [LibraryImport("wasmstatic")]
    internal static partial int ws_mul(int a, int b);

    /// <summary>a - b (WasmObjects, object files).</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    /// <returns>The difference.</returns>
    [LibraryImport("wasmobjects")]
    internal static partial int wo_sub(int a, int b);
}
