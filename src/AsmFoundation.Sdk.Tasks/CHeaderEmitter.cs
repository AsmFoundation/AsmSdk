using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AsmFoundation.Sdk.Bindings;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes the C header of a library or program from its API file: its STRUCTs as C structs with
/// compile-time checks of their size and offsets, and its functions' declarations (the owner's
/// choices of 2026-11-25 and 2026-11-26: "Declarations + layout checks", built by default).
/// </summary>
/// <remarks>
/// <para>
/// The checks hold every struct to the layout the SDK gave the asm code: a compiler that lays one
/// out otherwise (packing pragmas around the #include, another ABI, a 32-bit target) stops at the
/// header instead of misbehaving at run time. C11 and C++11 get <c>_Static_assert</c> and
/// <c>static_assert</c>; older C and C++ (and MSVC, whose <c>__cplusplus</c> says 199711L unless
/// told otherwise) a typedef of an array whose size is negative when the check fails. Every RID of
/// a library must agree on its structs (ASM0133), so one set of numbers serves them all; STRUCT
/// lines are refused in wasm sources, so no wasm32 layout ever needs other numbers.
/// </para>
/// <para>
/// Measured on 2026-11-26 with the prototype of this emitter (gcc 13.3 in WSL): BindingsPackage's,
/// Crc32C's and ApiStruct's headers compiled as C11, C99 and C++11 with -Wall -Wextra -pedantic
/// -Werror; C programs linked against the real libbindpkg.so and libcrc32c.so returned the right
/// values; one offset changed by hand stopped the C11 compile (static assertion failed) and the C99
/// one (size of array is negative). The prototype found the two traps this code avoids: the
/// prototype text keeps PROTO's "fast" (so the declarations are rendered from the model), and an
/// empty macro left a stray semicolon at file scope, which C99's -pedantic rejects.
/// </para>
/// </remarks>
internal static class CHeaderEmitter
{
    /// <summary>The header of a library: the whole file, "\n" line ends.</summary>
    /// <param name="library">The library, from its API file.</param>
    /// <param name="origin">What writes it, for the comment ("AsmFoundation.Sdk 0.4.0").</param>
    /// <returns>The header's text.</returns>
    internal static string Write(ApiLibraryModel library, string origin)
    {
        string identifier = Identifier(library.Library);
        string guard = identifier + "_H";
        string layout = identifier + "_LAYOUT";
        List<ApiStructModel> structs = Ordered(library.Structs);
        bool usesBool = library.Structs.Any(s => s.Fields.Any(f => UsesBool(f.Type)))
                        || library.Functions.Any(f => UsesBool(f.Returns) || f.Parameters.Any(p => UsesBool(p.Type)));

        var x = new StringBuilder();
        x.Append("/*\n");
        x.Append(" * ").Append(library.Library).Append(".h: the C declarations of ").Append(library.Library).Append(", written by ").Append(origin).Append('\n');
        x.Append(" * from its PROTO and STRUCT lines (").Append(library.Library).Append(".api.json). Do not edit: the next build writes it again.\n");
        x.Append(" */\n");
        x.Append("#ifndef ").Append(guard).Append('\n');
        x.Append("#define ").Append(guard).Append("\n\n");
        if (usesBool)
        {
            x.Append("#include <stdbool.h>\n");
        }

        x.Append("#include <stddef.h>\n");
        x.Append("#include <stdint.h>\n\n");
        x.Append("#ifdef __cplusplus\n");
        x.Append("extern \"C\" {\n");
        x.Append("#endif\n\n");

        if (structs.Count > 0)
        {
            x.Append("/* Each struct's size and offsets, as the SDK laid them out for the asm code (every RID of the\n");
            x.Append("   library agrees): a compiler that lays one out otherwise stops here, not at run time. */\n");
            x.Append("#if defined(__cplusplus) && __cplusplus >= 201103L\n");
            x.Append("#define ").Append(layout).Append("(condition, message) static_assert(condition, message)\n");
            x.Append("#elif !defined(__cplusplus) && defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L\n");
            x.Append("#define ").Append(layout).Append("(condition, message) _Static_assert(condition, message)\n");
            x.Append("#else\n");
            x.Append("/* Older C and C++: an array of negative size stops the compile just the same. */\n");
            x.Append("#define ").Append(layout).Append("_JOIN2(a, b) a##b\n");
            x.Append("#define ").Append(layout).Append("_JOIN(a, b) ").Append(layout).Append("_JOIN2(a, b)\n");
            x.Append("#define ").Append(layout).Append("(condition, message) typedef char ").Append(layout).Append("_JOIN(")
                .Append(identifier.ToLowerInvariant()).Append("_layout_, __LINE__)[(condition) ? 1 : -1]\n");
            x.Append("#endif\n\n");

            // Every struct named first, so a field may point at any of them (a list's next).
            foreach (ApiStructModel model in structs.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                x.Append("typedef struct ").Append(model.Name).Append(' ').Append(model.Name).Append(";\n");
            }

            x.Append('\n');
            foreach (ApiStructModel model in structs)
            {
                x.Append("/* STRUCT ").Append(model.Declaration).Append(": ").Append(model.Size).Append(" bytes, aligned to ").Append(model.Alignment).Append(". */\n");
                x.Append("struct ").Append(model.Name).Append(" {\n");
                foreach (ApiStructFieldModel field in model.Fields)
                {
                    x.Append("    ").Append(Field(field)).Append("; /* offset ").Append(field.Offset).Append(" */\n");
                }

                x.Append("};\n");
                x.Append(layout).Append("(sizeof(").Append(model.Name).Append(") == ").Append(model.Size)
                    .Append(", \"").Append(model.Name).Append(": ").Append(model.Size).Append(" bytes in the asm code\");\n");
                foreach (ApiStructFieldModel field in model.Fields.Where(f => f.Offset != 0))
                {
                    x.Append(layout).Append("(offsetof(").Append(model.Name).Append(", ").Append(field.Name).Append(") == ").Append(field.Offset)
                        .Append(", \"").Append(model.Name).Append('.').Append(field.Name).Append(": offset ").Append(field.Offset).Append(" in the asm code\");\n");
                }

                x.Append('\n');
            }
        }

        foreach (IGrouping<string, ApiFunctionModel> file in library.Functions.GroupBy(f => f.File).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            x.Append("/* ").Append(file.Key).Append(" */\n");
            foreach (ApiFunctionModel function in file.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                x.Append(Declaration(function)).Append(';');
                if (function.Fast)
                {
                    x.Append(" /* fast: short, never blocks */");
                }

                x.Append('\n');
            }

            x.Append('\n');
        }

        x.Append("#ifdef __cplusplus\n");
        x.Append("}\n");
        x.Append("#endif\n\n");
        x.Append("#endif /* ").Append(guard).Append(" */\n");
        return x.ToString();
    }

    /// <summary>
    /// A library's name as part of a C identifier, upper case: every character that is not an
    /// ASCII letter or digit becomes "_" ("Demo.Native" is DEMO_NATIVE), and a leading digit gets
    /// one in front.
    /// </summary>
    /// <param name="library">The library's name.</param>
    /// <returns>The identifier part.</returns>
    internal static string Identifier(string library)
    {
        var text = new StringBuilder();
        foreach (char c in library)
        {
            text.Append(c < 128 && char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return text.Length > 0 && char.IsDigit(text[0]) ? "_" + text : text.ToString();
    }

    /// <summary>The structs in an order C can define them: each after the structs it holds by value.</summary>
    /// <param name="structs">The library's structs (the API file keeps them by name).</param>
    /// <returns>The structs, dependencies first, otherwise by name.</returns>
    private static List<ApiStructModel> Ordered(IEnumerable<ApiStructModel> structs)
    {
        var byName = structs.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ApiStructModel>();

        void Visit(ApiStructModel model)
        {
            if (!done.Add(model.Name))
            {
                return;
            }

            // A struct held by value must be complete first; pointers need only the typedef.
            foreach (ApiStructFieldModel field in model.Fields)
            {
                if (field.Type.IsStruct && field.Type.Pointers == 0 && field.Type.Callback is null && byName.TryGetValue(field.Type.Base, out ApiStructModel? held))
                {
                    Visit(held);
                }
            }

            ordered.Add(model);
        }

        foreach (ApiStructModel model in byName.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            Visit(model);
        }

        return ordered;
    }

    /// <summary>A type as C writes it: const, the base, the pointers ("const uint8_t*").</summary>
    /// <param name="type">The type (not a function pointer).</param>
    /// <returns>The C type.</returns>
    private static string Type(ApiTypeModel type) => (type.Const ? "const " : "") + type.Base + new string('*', type.Pointers);

    /// <summary>A declarator of a named or unnamed thing of a type, function pointers included ("int32_t (*op)(int32_t, int32_t)").</summary>
    /// <param name="type">The type.</param>
    /// <param name="name">The name, or null for none (an unnamed parameter).</param>
    /// <returns>The C declaration.</returns>
    private static string Declarator(ApiTypeModel type, string? name)
    {
        if (type.Callback is { } callback)
        {
            return Type(callback.Returns) + " (*" + (name ?? "") + ")(" + Parameters(callback.Parameters) + ")";
        }

        return name is null ? Type(type) : Type(type) + " " + name;
    }

    /// <summary>A struct field's declaration (an array with its count).</summary>
    /// <param name="field">The field.</param>
    /// <returns>The C declaration, without the semicolon.</returns>
    private static string Field(ApiStructFieldModel field) =>
        Declarator(field.Type, field.Name) + (field.Count is { } count ? "[" + count + "]" : "");

    /// <summary>A parameter list: "void" for none, as C declares a function that takes none.</summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The list's text.</returns>
    private static string Parameters(IEnumerable<ApiParameterModel> parameters)
    {
        string text = string.Join(", ", parameters.Select(p => Declarator(p.Type, p.Name)));
        return text.Length == 0 ? "void" : text;
    }

    /// <summary>
    /// A function's declaration, from the model rather than the PROTO text (which keeps "fast"):
    /// a function that returns a function pointer is written the way C nests it,
    /// int32_t (*pick(int32_t which))(int32_t).
    /// </summary>
    /// <param name="function">The function.</param>
    /// <returns>The declaration, without the semicolon.</returns>
    private static string Declaration(ApiFunctionModel function)
    {
        string call = function.Name + "(" + Parameters(function.Parameters) + ")";
        if (function.Returns.Callback is { } callback)
        {
            return Type(callback.Returns) + " (*" + call + ")(" + Parameters(callback.Parameters) + ")";
        }

        return Type(function.Returns) + " " + call;
    }

    /// <summary>Whether a type uses bool, its own or a function pointer's (stdbool.h then).</summary>
    /// <param name="type">The type.</param>
    /// <returns>True when bool appears in it.</returns>
    private static bool UsesBool(ApiTypeModel type) =>
        type.Base == "bool"
        || (type.Callback is { } callback && (UsesBool(callback.Returns) || callback.Parameters.Any(p => UsesBool(p.Type))));
}
