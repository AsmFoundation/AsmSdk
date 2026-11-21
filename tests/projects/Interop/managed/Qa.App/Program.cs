using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AsmQa.Interop;

namespace AsmQa.App;

/// <summary>
/// The interop tour: every interop technique against the asm libraries, each compared with a C#
/// reference on fixed-seed random data, then concurrency and large-buffer checks. The exit code is
/// the number of failed checks.
/// </summary>
internal static unsafe class Program
{
    /// <summary>One seed for every run, so a failure reproduces.</summary>
    private static readonly Random Random = new(20261120);

    /// <summary>Runs the checks and prints the summary.</summary>
    /// <returns>The number of failed checks.</returns>
    private static int Main()
    {
        string rid = RuntimeInformation.RuntimeIdentifier;
        Console.WriteLine($"AsmQa on {rid} ({RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
        Console.WriteLine($"app folder: {AppContext.BaseDirectory}");

        // ASMQA_GROUPS=a,b runs only those groups (for bisecting a platform-specific failure);
        // unset runs everything.
        string[]? only = Environment.GetEnvironmentVariable("ASMQA_GROUPS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Wanted(string group) => only is null || only.Contains(group, StringComparer.OrdinalIgnoreCase);

        var checks = new Checks();
        if (Wanted("libraryimport"))
        {
            LibraryImports(checks, rid);
        }

        if (Wanted("dllimport"))
        {
            ClassicImports(checks);
        }

        if (Wanted("dynamic"))
        {
            DynamicLoading(checks);
        }

        if (Wanted("alias"))
        {
            Alias(checks);
        }

        if (Wanted("libc"))
        {
            Libc(checks);
        }

        if (Wanted("threads"))
        {
            Concurrency(checks);
        }

        if (Wanted("large"))
        {
            LargeData(checks);
        }

        Console.WriteLine($"{checks.Passed} passed, {checks.Failed} failed");
        return checks.Failed;
    }

    /// <summary>Source-generated P/Invoke: every ABI shape of QaMath.</summary>
    /// <param name="checks">The harness.</param>
    /// <param name="rid">This process's RID, which the loaded library must have been built for.</param>
    private static void LibraryImports(Checks checks, string rid)
    {
        checks.Run("LibraryImport: the loaded qamath is this RID's build", () => Checks.Same($"qamath 1.0 {rid}", QaMath.Version));
        checks.Run("LibraryImport: qa_add", () => Checks.Same(42, QaMath.Add(40, 2)));
        checks.Run("LibraryImport: qa_add wraps like C's int32", () => Checks.Same(int.MinValue, QaMath.Add(int.MaxValue, 1)));
        checks.Run("LibraryImport + SuppressGCTransition: qa_add", () => Checks.Same(-5, QaMath.AddNoTransition(-7, 2)));
        checks.Run("LibraryImport: ReadOnlySpan<long> pinned (qa_sum_i64, 10,000 values)", () =>
        {
            long[] values = RandomLongs(10_000);
            return Checks.Same(ManagedSum(values), QaMath.SumI64(values, (nuint)values.Length));
        });
        checks.Run("LibraryImport: an empty span", () => Checks.Same(0L, QaMath.SumI64(ReadOnlySpan<long>.Empty, 0)));
        checks.Run("LibraryImport: interleaved long/double arguments (qa_mix, 200 cases)", () =>
        {
            for (int i = 0; i < 200; i++)
            {
                long a = Random.NextInt64(-1_000_000, 1_000_000), c = Random.NextInt64(-1_000_000, 1_000_000);
                double b = (Random.NextDouble() * 1000) - 500, d = (Random.NextDouble() * 1000) - 500;
                double expected = (a * b) + (c * d), actual = QaMath.Mix(a, b, c, d);
                if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual))
                {
                    return $"qa_mix({a}, {b}, {c}, {d}) = {actual}, expected {expected}";
                }
            }

            return null;
        });
        checks.Run("LibraryImport: eight arguments, the last ones on the stack (qa_many, 100 cases)", () =>
        {
            for (int i = 0; i < 100; i++)
            {
                long[] a = RandomLongs(8);
                long expected = unchecked(a[0] + (2 * a[1]) + (3 * a[2]) + (4 * a[3]) + (5 * a[4]) + (6 * a[5]) + (7 * a[6]) + (8 * a[7]));
                long actual = QaMath.Many(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
                if (expected != actual)
                {
                    return $"case {i}: expected {expected}, got {actual}";
                }
            }

            return null;
        });
        checks.Run("LibraryImport: structs by reference (qa_dot3, `in`)", () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var a = new Vec3i { X = Random.Next(), Y = -Random.Next(), Z = Random.Next() };
                var b = new Vec3i { X = Random.Next(), Y = Random.Next(), Z = -Random.Next() };
                long expected = unchecked(((long)a.X * b.X) + ((long)a.Y * b.Y) + ((long)a.Z * b.Z));
                if (QaMath.Dot3(a, b) != expected)
                {
                    return $"case {i}: expected {expected}, got {QaMath.Dot3(a, b)}";
                }
            }

            return null;
        });
        checks.Run("LibraryImport: an 8-byte struct by value (qa_pair_sum)", () =>
        {
            var pair = new Pair { A = -123_456, B = int.MaxValue };
            return Checks.Same((long)pair.A + pair.B, QaMath.PairSum(pair));
        });
        checks.Run("LibraryImport: an 8-byte struct returned (qa_make_pair)", () =>
        {
            Pair pair = QaMath.MakePair(-7, 1_000_000_007);
            return pair.A == -7 && pair.B == 1_000_000_007 ? null : $"got {{{pair.A}, {pair.B}}}";
        });
        checks.Run("LibraryImport: a 16-byte struct returned (qa_make_quad; a hidden pointer on Windows x64)", () =>
        {
            Quad16 quad = QaMath.MakeQuad(long.MinValue, 0x0123_4567_89AB_CDEF);
            return quad.X == long.MinValue && quad.Y == 0x0123_4567_89AB_CDEF ? null : $"got {{{quad.X}, {quad.Y}}}";
        });
        checks.Run("LibraryImport: callbacks into C# ([UnmanagedCallersOnly] function pointers, qa_reduce)", () =>
        {
            int[] values = RandomInts(5_000);
            long sum = QaMath.Reduce(values, (nuint)values.Length, QaCallbacks.SumPointer);
            long max = QaMath.Reduce(values, (nuint)values.Length, QaCallbacks.MaxPointer);
            return Checks.Same(values.Sum(v => (long)v), sum) ?? Checks.Same(Math.Max(0L, values.Max()), max);
        });
        checks.Run("LibraryImport: a callback that runs a full GC mid-call (pinned span stays put)", () =>
        {
            int[] values = RandomInts(3_000);
            long sum = QaMath.Reduce(values, (nuint)values.Length, &SumWithCollections);
            return Checks.Same(values.Sum(v => (long)v), sum);
        });
        checks.Run("LibraryImport: UTF-8 strings (qa_utf8_count)", () =>
        {
            foreach (string text in new[] { "", "ascii", "héllo wörld", "😀 emoji ∑ math", "日本語のテキスト" })
            {
                if (QaMath.Utf8Count(text) != text.EnumerateRunes().Count())
                {
                    return $"\"{text}\": got {QaMath.Utf8Count(text)}";
                }
            }

            return null;
        });
        checks.Run("LibraryImport: a caller-owned buffer changed in place (qa_upper_ascii)", () =>
        {
            byte[] text = Encoding.UTF8.GetBytes("Hello, wörld! abc xyz {|}~ `@");
            byte[] expected = text.Select(b => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b).ToArray();
            int changes = text.Zip(expected).Count(p => p.First != p.Second);
            nuint changed = QaMath.UpperAscii(text, (nuint)text.Length);
            return Checks.Same((nuint)changes, changed) ?? (text.SequenceEqual(expected) ? null : Encoding.UTF8.GetString(text));
        });
        checks.Run("LibraryImport: out parameters and a status (qa_divmod)", () =>
        {
            foreach ((int a, int b) in new[] { (17, 5), (-17, 5), (17, -5), (int.MaxValue, 7) })
            {
                if (QaMath.DivMod(a, b, out int q, out int r) != 0 || q != a / b || r != a % b)
                {
                    return $"{a} / {b}: got {q} r {r}";
                }
            }

            return Checks.Same(-1, QaMath.DivMod(1, 0, out _, out _));
        });
        checks.Run("LibraryImport: a 1-byte bool return (qa_is_even)", () =>
        {
            foreach (long value in RandomLongs(200))
            {
                if (QaMath.IsEven(value) != (value % 2 == 0))
                {
                    return $"{value}: got {QaMath.IsEven(value)}";
                }
            }

            return null;
        });
        checks.Run("LibraryImport: SIMD in asm, Span<float> (qa_add_f32, 1,003 floats)", () =>
        {
            float[] a = RandomFloats(1_003), b = RandomFloats(1_003), sum = new float[1_003];
            QaMath.AddF32(a, b, sum, (nuint)sum.Length);
            for (int i = 0; i < sum.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(a[i] + b[i]) != BitConverter.SingleToInt32Bits(sum[i]))
                {
                    return $"[{i}]: {a[i]} + {b[i]} = {sum[i]}";
                }
            }

            return null;
        });
        checks.Run("LibraryImport: a hidden helper of a linked asm static library (qa_sum_squares)", () =>
        {
            long[] values = Enumerable.Range(0, 1_000).Select(_ => Random.NextInt64(-1_000_000, 1_000_000)).ToArray();
            return Checks.Same(values.Sum(v => v * v), QaMath.SumSquares(values, (nuint)values.Length));
        });
    }

    /// <summary>Classic runtime-marshalled P/Invoke.</summary>
    /// <param name="checks">The harness.</param>
    private static void ClassicImports(Checks checks)
    {
        checks.Run("DllImport: qa_add (Cdecl)", () => Checks.Same(42, QaMathClassic.Add(50, -8)));
        checks.Run("DllImport: long[] pinned by the runtime", () =>
        {
            long[] values = RandomLongs(1_000);
            return Checks.Same(ManagedSum(values), QaMathClassic.SumI64(values, (nuint)values.Length));
        });
        checks.Run("DllImport: [MarshalAs(LPUTF8Str)] string", () => Checks.Same(9L, QaMathClassic.Utf8Count("çà et là!")));
        checks.Run("DllImport: [In, Out] byte[] changed in place", () =>
        {
            byte[] text = "mixed Case 123"u8.ToArray();
            nuint changed = QaMathClassic.UpperAscii(text, (nuint)text.Length);
            return Checks.Same((nuint)8, changed) ?? Checks.Same("MIXED CASE 123", Encoding.ASCII.GetString(text));
        });
        checks.Run("DllImport: a managed closure as the callback (delegate marshalling)", () =>
        {
            int[] values = RandomInts(2_000);
            long factor = 3;
            int calls = 0;
            long result = QaMathClassic.Reduce(values, (nuint)values.Length, (acc, value) =>
            {
                calls++;
                return acc + (value * factor);
            });
            return Checks.Same(values.Sum(v => v * factor), result) ?? Checks.Same(values.Length, calls);
        });
        checks.Run("DllImport: Marshal.GetFunctionPointerForDelegate + GC.KeepAlive", () =>
        {
            int[] values = RandomInts(2_000);
            QaMathClassic.ReduceOp xor = (acc, value) => acc ^ value;
            nint pointer = Marshal.GetFunctionPointerForDelegate(xor);
            long result = QaMathClassic.ReducePointer(values, (nuint)values.Length, pointer);
            GC.KeepAlive(xor);
            return Checks.Same(values.Aggregate(0L, (acc, value) => acc ^ value), result);
        });
        checks.Run("DllImport: ref structs", () =>
        {
            var a = new Vec3i { X = 1, Y = 2, Z = 3 };
            var b = new Vec3i { X = 4, Y = -5, Z = 6 };
            return Checks.Same(12L, QaMathClassic.Dot3(ref a, ref b));
        });
        checks.Run("DllImport: out parameters", () =>
            QaMathClassic.DivMod(-100, 7, out int q, out int r) == 0 && q == -14 && r == -2 ? null : $"got {q} r {r}");
        checks.Run("DllImport: [return: MarshalAs(I1)] bool", () =>
            QaMathClassic.IsEven(-4) && !QaMathClassic.IsEven(9) ? null : "wrong parity");
        checks.Run("DllImport: a 16-byte struct returned", () =>
        {
            Quad16 quad = QaMathClassic.MakeQuad(-1, 2);
            return quad.X == -1 && quad.Y == 2 ? null : $"got {{{quad.X}, {quad.Y}}}";
        });
        checks.Run("DllImport: interleaved long/double arguments", () => Checks.Same((3 * 1.5) + (4 * 0.25), QaMathClassic.Mix(3, 1.5, 4, 0.25)));
    }

    /// <summary>NativeLibrary.Load + GetExport: unmanaged function pointers and runtime-built delegates.</summary>
    /// <param name="checks">The harness.</param>
    private static void DynamicLoading(Checks checks)
    {
        checks.Run("NativeLibrary + delegate* unmanaged: qa_add", () => Checks.Same(42, QaMathDynamic.Add(21, 21)));
        checks.Run("NativeLibrary + delegate* unmanaged: eight arguments", () =>
            Checks.Same(1L + 4 + 9 + 16 + 25 + 36 + 49 + 64, QaMathDynamic.Many(1, 2, 3, 4, 5, 6, 7, 8)));
        checks.Run("NativeLibrary + delegate* unmanaged: an 8-byte struct returned", () =>
        {
            Pair pair = QaMathDynamic.MakePair(int.MinValue, -1);
            return pair.A == int.MinValue && pair.B == -1 ? null : $"got {{{pair.A}, {pair.B}}}";
        });
        checks.Run("NativeLibrary + delegate* unmanaged: a 16-byte struct returned", () =>
        {
            Quad16 quad = QaMathDynamic.MakeQuad(5, -6);
            return quad.X == 5 && quad.Y == -6 ? null : $"got {{{quad.X}, {quad.Y}}}";
        });
        checks.Run("Marshal.GetDelegateForFunctionPointer: qa_mix", () => Checks.Same((-2 * 0.5) + (10 * 1.25), QaMathDynamic.Mix(-2, 0.5, 10, 1.25)));
        checks.Run("Marshal.GetDelegateForFunctionPointer: a 16-byte struct returned", () =>
        {
            Quad16 quad = QaMathDynamic.MakeQuadDelegate(long.MaxValue, long.MinValue);
            return quad.X == long.MaxValue && quad.Y == long.MinValue ? null : $"got {{{quad.X}, {quad.Y}}}";
        });
        checks.Run("NativeLibrary.TryGetExport: exported, hidden and missing names", () =>
            QaMathDynamic.HasExport("qa_add") && !QaMathDynamic.HasExport("qa_helper_square") && !QaMathDynamic.HasExport("qa_missing")
                ? null
                : $"qa_add {QaMathDynamic.HasExport("qa_add")}, qa_helper_square {QaMathDynamic.HasExport("qa_helper_square")}, qa_missing {QaMathDynamic.HasExport("qa_missing")}");
    }

    /// <summary>A DllImport resolver mapping a logical name to the asm library.</summary>
    /// <param name="checks">The harness.</param>
    private static void Alias(Checks checks)
    {
        checks.Run("SetDllImportResolver: LibraryImport(\"qa-alias\")", () => Checks.Same(42, QaAlias.Add(20, 22)));
        checks.Run("SetDllImportResolver: DllImport(\"qa-alias\")", () => QaAlias.IsEven(10) && !QaAlias.IsEven(11) ? null : "wrong parity");
        checks.Run("SetDllImportResolver: the resolver ran", () => QaAlias.Resolutions >= 1 ? null : "never called");
    }

    /// <summary>QaLibc, which exists for the x64 RIDs only.</summary>
    /// <param name="checks">The harness.</param>
    private static void Libc(Checks checks)
    {
        bool expected = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        checks.Run("NativeLibrary.TryLoad: QaLibc exists exactly on the x64 RIDs", () => Checks.Same(expected, QaLibc.IsAvailable));
        if (!QaLibc.IsAvailable)
        {
            checks.Run("optional component: calling QaLibc where it is absent throws DllNotFoundException", () =>
            {
                try
                {
                    QaLibc.Format(1);
                    return "no exception";
                }
                catch (DllNotFoundException)
                {
                    return null;
                }
            });
            return;
        }

        checks.Run("libc: malloc'ed string owned by a SafeHandle, freed by the library (qa_dup_reverse)", () => Checks.Same("!msa ,olleH", QaLibc.Reverse("Hello, asm!")));
        checks.Run("libc: 10,000 allocations through malloc/free", () =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                string text = $"n{i}";
                if (QaLibc.Reverse(text) != new string(text.Reverse().ToArray()))
                {
                    return $"iteration {i}";
                }
            }

            return null;
        });
        checks.Run("libc: snprintf of the platform's C library (qa_format)", () => Checks.Same("value=-1234567890123", QaLibc.Format(-1_234_567_890_123)));
        checks.Run("libc: qsort with an asm comparator (qa_sort_i32)", () =>
        {
            int[] values = RandomInts(1_000), sorted = values.Order().ToArray();
            QaLibc.Sort(values);
            return values.SequenceEqual(sorted) ? null : "not sorted";
        });
    }

    /// <summary>Calls from many threads at once.</summary>
    /// <param name="checks">The harness.</param>
    private static void Concurrency(Checks checks)
    {
        checks.Run("threads: 8 x 100,000 atomic increments in asm", () =>
        {
            long before = QaMath.CounterAdd(0);
            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    QaMath.CounterAdd(1);
                }
            });
            return Checks.Same(800_000L, QaMath.CounterAdd(0) - before);
        });
        checks.Run("threads: 16 concurrent folds calling back into C#", () =>
        {
            int[] values = RandomInts(10_000);
            long expected = values.Sum(v => (long)v);
            var results = new long[16];
            Parallel.For(0, results.Length, i => results[i] = QaMath.Reduce(values, (nuint)values.Length, QaCallbacks.SumPointer));
            return results.All(r => r == expected) ? null : string.Join(", ", results);
        });
        checks.Run("tasks: a call from a thread-pool task", () => Checks.Same(3, Task.Run(() => QaMath.Add(1, 2)).GetAwaiter().GetResult()));
    }

    /// <summary>Large buffers, timed against the C# loop (the timings are printed, not checked).</summary>
    /// <param name="checks">The harness.</param>
    private static void LargeData(Checks checks)
    {
        checks.Run("64 MB: qa_sum_i64 over 8,388,608 longs", () =>
        {
            long[] values = RandomLongs(8 * 1024 * 1024);
            var watch = Stopwatch.StartNew();
            long native = QaMath.SumI64(values, (nuint)values.Length);
            double nativeMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            long managed = ManagedSum(values);
            Console.WriteLine($"      asm {nativeMs:F1} ms, C# {watch.Elapsed.TotalMilliseconds:F1} ms");
            return Checks.Same(managed, native);
        });
        checks.Run("4 MB: qa_add_f32 over 1,048,576 floats", () =>
        {
            float[] a = RandomFloats(1 << 20), b = RandomFloats(1 << 20), sum = new float[1 << 20];
            var watch = Stopwatch.StartNew();
            QaMath.AddF32(a, b, sum, (nuint)sum.Length);
            Console.WriteLine($"      asm {watch.Elapsed.TotalMilliseconds:F2} ms");
            for (int i = 0; i < sum.Length; i += 4_099)
            {
                if (sum[i] != a[i] + b[i])
                {
                    return $"[{i}]";
                }
            }

            return null;
        });
    }

    /// <summary>
    /// A fold step that forces a full, compacting GC whenever the accumulator is a multiple of 500
    /// (on the first call, and a few times per run): the pinned input must not move under the asm.
    /// </summary>
    /// <param name="acc">The accumulator.</param>
    /// <param name="value">The next value.</param>
    /// <returns>acc + value.</returns>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static long SumWithCollections(long acc, int value)
    {
        if (acc % 500 == 0)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        return acc + value;
    }

    /// <summary>The wrapping sum C#'s way, the reference for qa_sum_i64.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The sum.</returns>
    private static long ManagedSum(long[] values)
    {
        long sum = 0;
        foreach (long value in values)
        {
            sum = unchecked(sum + value);
        }

        return sum;
    }

    /// <summary>Random 64-bit values over the whole range.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The values.</returns>
    private static long[] RandomLongs(int count) => Enumerable.Range(0, count).Select(_ => Random.NextInt64(long.MinValue, long.MaxValue)).ToArray();

    /// <summary>Random 32-bit values over the whole range.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The values.</returns>
    private static int[] RandomInts(int count) => Enumerable.Range(0, count).Select(_ => Random.Next(int.MinValue, int.MaxValue)).ToArray();

    /// <summary>Random floats in [-1000, 1000).</summary>
    /// <param name="count">How many.</param>
    /// <returns>The values.</returns>
    private static float[] RandomFloats(int count) => Enumerable.Range(0, count).Select(_ => (Random.NextSingle() * 2000) - 1000).ToArray();
}
