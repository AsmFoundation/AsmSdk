using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AsmFoundation.Sdk.Bindings;

/// <summary>How a class lays out the functions it binds (the attribute's AsmNesting).</summary>
internal enum Nesting
{
    /// <summary>A nested class per .asm file, folders as levels (the default).</summary>
    Files = 0,

    /// <summary>A nested class per folder; a folder's files share it.</summary>
    Folders = 1,

    /// <summary>Every function directly in the class.</summary>
    Flat = 2,
}

/// <summary>
/// Writes the C# of a bound class's members: one blittable [DllImport] extern per function (with
/// a wrapper when it takes a bool or a string), span overloads, nested classes per file or
/// folder. Pure text, no Roslyn: what the generator decided (which functions, which layout) comes
/// in, source comes out.
/// </summary>
/// <remarks>
/// <para>
/// The C types map to the blittable C# types of the same size and signedness
/// (<see cref="BaseType"/>), so each call is direct, with no runtime marshalling. Two kinds
/// convert in a generated wrapper on purpose: a <c>bool</c> is one byte (C's bool), a
/// <c>const char*</c> parameter is a <c>string</c> converted to UTF-8; inside a function
/// pointer's signature neither converts (an unmanaged function pointer cannot), so there they
/// stay <c>byte</c> and <c>byte*</c>.
/// </para>
/// <para>
/// Footgun: the members are <c>unsafe</c> when their signature has a pointer, and the string
/// wrappers use pointers, so the project needs AllowUnsafeBlocks; the SDK's consumer targets
/// set it whenever bindings are generated.
/// </para>
/// </remarks>
internal static class CSharpEmitter
{
    /// <summary>The C# keywords a C parameter name may collide with: written with an @.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>The length parameter names the span convention accepts after a pointer (also the pointer's name plus _len, _length, _size or _count).</summary>
    private static readonly HashSet<string> LengthNames = new(StringComparer.Ordinal) { "len", "length", "size", "count" };

    /// <summary>The fully qualified attribute namespace every generated attribute uses (no using directives in generated code).</summary>
    private const string Interop = "global::System.Runtime.InteropServices";

    /// <summary>
    /// PascalCase of a C name or a file name: split at anything that is not a letter or digit,
    /// each part's first letter upper case, joined ("add_ints" AddInts, "crc32c_append"
    /// Crc32cAppend, "my-crc.native" MyCrcNative). A leading digit gets an underscore.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>A C# identifier.</returns>
    internal static string Pascal(string name)
    {
        var result = new StringBuilder();
        foreach (string part in name.Split(c => !char.IsLetterOrDigit(c)))
        {
            result.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        }

        if (result.Length == 0)
        {
            return "_";
        }

        return char.IsDigit(result[0]) ? "_" + result : result.ToString();
    }

    /// <summary>
    /// Writes the members of a bound class, laid out by <paramref name="nesting"/>, and reports
    /// each name that would be generated twice in one scope.
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The indentation of the members (four spaces per level).</param>
    /// <param name="library">The import name.</param>
    /// <param name="functions">The functions this class binds.</param>
    /// <param name="nesting">The layout.</param>
    /// <param name="spanOverloads">Whether pointer-and-length pairs get span overloads.</param>
    /// <param name="access">"internal" or "public", for the members and the nested classes.</param>
    /// <param name="className">The bound class's own name: a function named like the class it lands in becomes Invoke (C# forbids a member named like its type, CS0542).</param>
    /// <param name="collisions">Receives a description of each name generated twice in one scope.</param>
    internal static void WriteMembers(StringBuilder output, int indent, string library, IEnumerable<ApiFunctionModel> functions,
        Nesting nesting, bool spanOverloads, string access, string className, List<string> collisions)
    {
        var root = new Node("");
        foreach (ApiFunctionModel function in functions.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            string[] path = function.File.Split('/');
            IEnumerable<string> levels = nesting switch
            {
                Nesting.Files => path,
                Nesting.Folders => path.Take(path.Length - 1),
                _ => Enumerable.Empty<string>(),
            };
            Node node = root;
            foreach (string level in levels)
            {
                node = node.Child(Pascal(level), level);
            }

            node.Functions.Add(function);
        }

        WriteNode(output, indent, library, root, className, spanOverloads, access, collisions, "");
    }

    /// <summary>Writes one scope: its methods, then its nested classes.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The members' indentation.</param>
    /// <param name="library">The import name.</param>
    /// <param name="node">The scope.</param>
    /// <param name="scopeName">The class name of the scope (the bound class's for the root).</param>
    /// <param name="spanOverloads">Whether to write span overloads.</param>
    /// <param name="access">The members' accessibility.</param>
    /// <param name="collisions">Receives names generated twice.</param>
    /// <param name="scope">The scope's path, for messages ("" for the class itself).</param>
    private static void WriteNode(StringBuilder output, int indent, string library, Node node, string scopeName, bool spanOverloads, string access,
        List<string> collisions, string scope)
    {
        // Two functions whose PascalCase names meet, or a function named like a nested class,
        // would be a C# error in the generated code; reported instead, at the attribute.
        var methodNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ApiFunctionModel function in node.Functions)
        {
            string method = Pascal(function.Name);
            // A function named like the class it lands in (answer in answer.asm, nested by
            // file) cannot keep its name: C# forbids a member named like its type (CS0542). It
            // becomes Invoke, which reads as calling the file's function and stays put when the
            // file gains functions; its summary keeps the C prototype.
            if (method == scopeName)
            {
                method = "Invoke";
            }

            if (methodNames.TryGetValue(method, out string? other))
            {
                collisions.Add($"{function.Name} and {other} both become {scope}{method}");
                continue;
            }

            if (node.Children.ContainsKey(method))
            {
                collisions.Add($"{function.Name} becomes {scope}{method}, the name of a nested class");
                continue;
            }

            methodNames[method] = function.Name;
            WriteFunction(output, indent, library, function, method, spanOverloads, access);
        }

        foreach (KeyValuePair<string, Node> child in node.Children)
        {
            Indent(output, indent).Append("/// <summary>The functions of ").Append(child.Value.Source).Append(".</summary>\n");
            Indent(output, indent).Append(access).Append(" static partial class ").Append(child.Key).Append('\n');
            Indent(output, indent).Append("{\n");
            WriteNode(output, indent + 1, library, child.Value, child.Key, spanOverloads, access, collisions, scope + child.Key + ".");
            Indent(output, indent).Append("}\n");
        }
    }

    /// <summary>
    /// Writes a function's binding and, by the convention, its span overload: a blittable
    /// [DllImport] extern, and when the signature has a bool or a string, a wrapper that converts
    /// them (the extern is then private, named __Name_Native).
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The indentation.</param>
    /// <param name="library">The import name.</param>
    /// <param name="function">The function.</param>
    /// <param name="method">Its C# name.</param>
    /// <param name="spanOverloads">Whether to write a span overload.</param>
    /// <param name="access">The accessibility.</param>
    /// <remarks>
    /// Why [DllImport] and not [LibraryImport]: source generators never see each other's output,
    /// so the runtime's LibraryImport generator would not implement [LibraryImport] methods this
    /// generator writes (measured: CS8795 on every one). The externs are blittable instead, and
    /// the conversions LibraryImport would have generated are written here: a string through
    /// Utf8StringMarshaller's stack-buffer path (on the stack when short, as LibraryImport does),
    /// a bool as one byte. For a blittable signature LibraryImport emits this same DllImport
    /// (measured with the Crc32C sample), so nothing is lost; no runtime marshalling is used, so
    /// it works under DisableRuntimeMarshalling and in NativeAOT.
    /// </remarks>
    private static void WriteFunction(StringBuilder output, int indent, string library, ApiFunctionModel function, string method,
        bool spanOverloads, string access)
    {
        string[] names = function.Parameters.Select((p, i) => ParameterName(p, i)).ToArray();
        bool wrapped = IsBool(function.Returns) || function.Parameters.Any(p => IsBool(p.Type) || IsString(p.Type));
        bool unsafeNative = HasPointer(function.Returns) || function.Parameters.Any(p => HasPointer(p.Type));
        string native = wrapped ? "__" + method + "_Native" : method;

        if (!wrapped)
        {
            Summary(output, indent, function);
        }

        Indent(output, indent).Append('[').Append(Interop).Append(".DllImport(\"").Append(library).Append("\", EntryPoint = \"")
            .Append(function.Name).Append("\", ExactSpelling = true)]\n");
        if (function.Fast)
        {
            // A short function that never blocks: no GC transition (3.38 ns per call instead
            // of 9.60 ns, measured with the Crc32C sample).
            Indent(output, indent).Append('[').Append(Interop).Append(".SuppressGCTransition]\n");
        }

        Indent(output, indent).Append(wrapped ? "private" : access).Append(" static ").Append(unsafeNative ? "unsafe " : "").Append("extern ")
            .Append(NativeType(function.Returns)).Append(' ').Append(native).Append('(');
        for (int i = 0; i < names.Length; i++)
        {
            output.Append(i == 0 ? "" : ", ").Append(NativeType(function.Parameters[i].Type)).Append(' ').Append(names[i]);
        }

        output.Append(");\n\n");

        if (wrapped)
        {
            WriteWrapper(output, indent, function, method, native, names, access);
        }

        if (spanOverloads)
        {
            WriteSpanOverload(output, indent, function, method, names, access);
        }
    }

    /// <summary>
    /// Writes the public method of a function whose signature has a bool or a string: each
    /// string converted to UTF-8 (Utf8StringMarshaller.ManagedToUnmanagedIn, a stack buffer of
    /// its BufferSize, freed after the call), each bool passed as 1 or 0, a bool result read as
    /// non-zero.
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The indentation.</param>
    /// <param name="function">The function.</param>
    /// <param name="method">Its C# name.</param>
    /// <param name="native">The private extern's name.</param>
    /// <param name="names">The parameters' C# names.</param>
    /// <param name="access">The accessibility.</param>
    private static void WriteWrapper(StringBuilder output, int indent, ApiFunctionModel function, string method, string native, string[] names, string access)
    {
        const string Marshaller = "global::System.Runtime.InteropServices.Marshalling.Utf8StringMarshaller.ManagedToUnmanagedIn";
        int[] strings = Enumerable.Range(0, names.Length).Where(i => IsString(function.Parameters[i].Type)).ToArray();
        bool unsafeWrapper = strings.Length > 0 || HasPointer(function.Returns) || function.Parameters.Any(p => HasPointer(p.Type) && !IsString(p.Type));

        Summary(output, indent, function);
        Indent(output, indent).Append(access).Append(" static ").Append(unsafeWrapper ? "unsafe " : "").Append(ReturnType(function.Returns))
            .Append(' ').Append(method).Append('(');
        for (int i = 0; i < names.Length; i++)
        {
            output.Append(i == 0 ? "" : ", ").Append(ParameterType(function.Parameters[i].Type)).Append(' ').Append(names[i]);
        }

        output.Append(")\n");
        Indent(output, indent).Append("{\n");
        int body = indent + 1;
        foreach (int i in strings)
        {
            // scoped, as LibraryImport declares it: the marshaller keeps the stack buffer, which
            // must not outlive this method (CS9081 without it, measured).
            Indent(output, body).Append("scoped ").Append(Marshaller).Append(" __s").Append(i).Append(" = new ").Append(Marshaller).Append("();\n");
            Indent(output, body).Append("__s").Append(i).Append(".FromManaged(").Append(names[i]).Append(", stackalloc byte[").Append(Marshaller).Append(".BufferSize]);\n");
        }

        var arguments = new StringBuilder();
        for (int i = 0; i < names.Length; i++)
        {
            arguments.Append(i == 0 ? "" : ", ");
            if (IsString(function.Parameters[i].Type))
            {
                arguments.Append("__s").Append(i).Append(".ToUnmanaged()");
            }
            else if (IsBool(function.Parameters[i].Type))
            {
                arguments.Append('(').Append(names[i]).Append(" ? (byte)1 : (byte)0)");
            }
            else
            {
                arguments.Append(names[i]);
            }
        }

        bool returnsVoid = function.Returns.Callback is null && function.Returns.Pointers == 0 && function.Returns.Base == "void";
        string call = native + "(" + arguments + ")";
        string statement = returnsVoid ? call + ";" : IsBool(function.Returns) ? "return " + call + " != 0;" : "return " + call + ";";
        if (strings.Length == 0)
        {
            Indent(output, body).Append(statement).Append('\n');
        }
        else
        {
            // Freed whatever the call does: a long string's buffer is native memory.
            Indent(output, body).Append("try\n");
            Indent(output, body).Append("{\n");
            Indent(output, body + 1).Append(statement).Append('\n');
            Indent(output, body).Append("}\n");
            Indent(output, body).Append("finally\n");
            Indent(output, body).Append("{\n");
            foreach (int i in strings)
            {
                Indent(output, body + 1).Append("__s").Append(i).Append(".Free();\n");
            }

            Indent(output, body).Append("}\n");
        }

        Indent(output, indent).Append("}\n\n");
    }

    /// <summary>Writes a binding's XML summary: the C prototype.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The indentation.</param>
    /// <param name="function">The function.</param>
    private static void Summary(StringBuilder output, int indent, ApiFunctionModel function) =>
        Indent(output, indent).Append("/// <summary><c>").Append(Xml(function.Prototype)).Append("</c></summary>\n");

    /// <summary>A type as the blittable extern sees it: a bool is a byte, a const char* a byte*.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The C# type.</returns>
    private static string NativeType(ApiTypeModel type) =>
        IsBool(type) ? "byte" : IsString(type) ? "byte*" : TypeName(type, inCallback: false);

    /// <summary>
    /// Writes the span overload of a function with pointer-and-length pairs (a pointer, then a
    /// size_t named len, length, size, count or the pointer's name plus _len, _length, _size or
    /// _count): each pair becomes one span, pinned for the call, its length passed in elements.
    /// Nothing when the function has no such pair.
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The indentation.</param>
    /// <param name="function">The function.</param>
    /// <param name="method">Its C# name.</param>
    /// <param name="names">Its parameters' C# names.</param>
    /// <param name="access">The accessibility.</param>
    private static void WriteSpanOverload(StringBuilder output, int indent, ApiFunctionModel function, string method, string[] names, string access)
    {
        var pairs = new Dictionary<int, int>();
        for (int i = 0; i + 1 < function.Parameters.Count; i++)
        {
            ApiTypeModel pointer = function.Parameters[i].Type;
            ApiParameterModel length = function.Parameters[i + 1];
            bool spannable = pointer.Callback is null && pointer.Pointers == 1 && pointer.Base != "void" && !IsString(pointer);
            if (spannable && length.Type.Callback is null && length.Type.Pointers == 0 && length.Type.Base == "size_t" && IsLengthName(length.Name, function.Parameters[i].Name))
            {
                pairs[i] = i + 1;
                i++;
            }
        }

        if (pairs.Count == 0)
        {
            return;
        }

        var lengths = new HashSet<int>(pairs.Values);
        Indent(output, indent).Append("/// <summary><c>").Append(Xml(function.Prototype)).Append("</c>, with spans for the pointer and length pairs.</summary>\n");
        Indent(output, indent).Append(access).Append(" static unsafe ").Append(ReturnType(function.Returns)).Append(' ').Append(method).Append('(');
        bool first = true;
        for (int i = 0; i < names.Length; i++)
        {
            if (lengths.Contains(i))
            {
                continue;
            }

            output.Append(first ? "" : ", ");
            first = false;
            ApiTypeModel type = function.Parameters[i].Type;
            if (pairs.ContainsKey(i))
            {
                output.Append(type.Const ? "global::System.ReadOnlySpan<" : "global::System.Span<").Append(BaseType(type.Base, inCallback: false)).Append("> ").Append(names[i]);
            }
            else
            {
                output.Append(ParameterType(type)).Append(' ').Append(names[i]);
            }
        }

        output.Append(")\n");
        Indent(output, indent).Append("{\n");
        int depth = indent + 1;
        foreach (int i in pairs.Keys)
        {
            Indent(output, depth).Append("fixed (").Append(BaseType(function.Parameters[i].Type.Base, inCallback: false)).Append("* __p").Append(i)
                .Append(" = ").Append(names[i]).Append(")\n");
        }

        Indent(output, depth).Append("{\n");
        Indent(output, depth + 1).Append(function.Returns.Callback is null && function.Returns.Pointers == 0 && function.Returns.Base == "void" ? "" : "return ")
            .Append(method).Append('(');
        for (int i = 0; i < names.Length; i++)
        {
            output.Append(i == 0 ? "" : ", ");
            if (pairs.ContainsKey(i))
            {
                output.Append("__p").Append(i);
            }
            else if (lengths.Contains(i))
            {
                output.Append("(nuint)").Append(names[i - 1]).Append(".Length");
            }
            else
            {
                output.Append(names[i]);
            }
        }

        output.Append(");\n");
        Indent(output, depth).Append("}\n");
        Indent(output, indent).Append("}\n\n");
    }

    /// <summary>Whether a parameter name says it is the length of the pointer before it.</summary>
    /// <param name="name">The length parameter's name, or null.</param>
    /// <param name="pointerName">The pointer parameter's name, or null.</param>
    /// <returns>True when the convention pairs them.</returns>
    private static bool IsLengthName(string? name, string? pointerName)
    {
        if (name is null)
        {
            return false;
        }

        if (LengthNames.Contains(name))
        {
            return true;
        }

        return pointerName is not null
            && (name == pointerName + "_len" || name == pointerName + "_length" || name == pointerName + "_size" || name == pointerName + "_count");
    }

    /// <summary>A parameter's C# name: its C name (an @ before a keyword), or arg0, arg1, ... for an unnamed one.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <param name="index">Its position.</param>
    /// <returns>The name.</returns>
    private static string ParameterName(ApiParameterModel parameter, int index) =>
        parameter.Name is null ? "arg" + index : Keywords.Contains(parameter.Name) ? "@" + parameter.Name : parameter.Name;

    /// <summary>A parameter's C# type: a const char* is a string (UTF-8), the rest as <see cref="TypeName"/>.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The C# type.</returns>
    private static string ParameterType(ApiTypeModel type) => IsString(type) ? "string" : TypeName(type, inCallback: false);

    /// <summary>A return type: a const char* stays byte* (who frees it is the library's rule).</summary>
    /// <param name="type">The type.</param>
    /// <returns>The C# type.</returns>
    private static string ReturnType(ApiTypeModel type) => TypeName(type, inCallback: false);

    /// <summary>The C# spelling of a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="inCallback">Inside a function pointer's signature, where nothing marshals (bool is byte).</param>
    /// <returns>The C# type.</returns>
    private static string TypeName(ApiTypeModel type, bool inCallback)
    {
        if (type.Callback is not null)
        {
            var parts = type.Callback.Parameters.Select(p => TypeName(p.Type, inCallback: true)).ToList();
            parts.Add(TypeName(type.Callback.Returns, inCallback: true));
            return "delegate* unmanaged<" + string.Join(", ", parts) + ">";
        }

        return BaseType(type.Base, inCallback) + new string('*', type.Pointers);
    }

    /// <summary>The C# type of a C base type: same size and signedness.</summary>
    /// <param name="baseType">The C base type.</param>
    /// <param name="inCallback">Inside a function pointer's signature: bool is byte there.</param>
    /// <returns>The C# type.</returns>
    /// <exception cref="ArgumentException">A base type the SDK's parser does not produce.</exception>
    internal static string BaseType(string baseType, bool inCallback) => baseType switch
    {
        "void" => "void",
        "bool" => inCallback ? "byte" : "bool",
        "char" => "byte",
        "int8_t" => "sbyte",
        "uint8_t" => "byte",
        "int16_t" => "short",
        "uint16_t" => "ushort",
        "int32_t" => "int",
        "uint32_t" => "uint",
        "int64_t" => "long",
        "uint64_t" => "ulong",
        "size_t" => "nuint",
        "ptrdiff_t" => "nint",
        "intptr_t" => "nint",
        "uintptr_t" => "nuint",
        "float" => "float",
        "double" => "double",
        _ => throw new ArgumentException($"unknown C base type '{baseType}'", nameof(baseType)),
    };

    /// <summary>Whether a type is a const char* (a C string, bound as a UTF-8 string parameter).</summary>
    /// <param name="type">The type.</param>
    /// <returns>True for const char*.</returns>
    private static bool IsString(ApiTypeModel type) => type.Callback is null && type.Base == "char" && type.Const && type.Pointers == 1;

    /// <summary>Whether a type is a plain bool (marshalled as one byte).</summary>
    /// <param name="type">The type.</param>
    /// <returns>True for bool.</returns>
    private static bool IsBool(ApiTypeModel type) => type.Callback is null && type.Base == "bool" && type.Pointers == 0;

    /// <summary>Whether a type is or holds a pointer (needs unsafe).</summary>
    /// <param name="type">The type.</param>
    /// <returns>True for pointers and function pointers.</returns>
    private static bool HasPointer(ApiTypeModel type) => type.Callback is not null || type.Pointers > 0;

    /// <summary>Escapes text for an XML doc comment.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    private static string Xml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Appends the indentation of a level.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="level">The level.</param>
    /// <returns>The output, for chaining.</returns>
    internal static StringBuilder Indent(StringBuilder output, int level) => output.Append(' ', level * 4);

    /// <summary>Splits a string at the characters a predicate selects, dropping empty parts.</summary>
    /// <param name="text">The string.</param>
    /// <param name="separator">Which characters separate.</param>
    /// <returns>The parts.</returns>
    private static IEnumerable<string> Split(this string text, Func<char, bool> separator)
    {
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || separator(text[i]))
            {
                if (i > start)
                {
                    yield return text.Substring(start, i - start);
                }

                start = i + 1;
            }
        }
    }

    /// <summary>One scope of the layout: functions and nested scopes.</summary>
    /// <param name="source">What the scope stands for, for its summary.</param>
    private sealed class Node(string source)
    {
        /// <summary>What the scope stands for (a file's or folder's name).</summary>
        internal string Source { get; } = source;

        /// <summary>The functions directly in this scope.</summary>
        internal List<ApiFunctionModel> Functions { get; } = new();

        /// <summary>The nested scopes, by class name, in the order first met.</summary>
        internal Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        /// <summary>The nested scope of a name, created on first use.</summary>
        /// <param name="name">The class name.</param>
        /// <param name="source">The file or folder name it stands for, for the class's summary.</param>
        /// <returns>The scope.</returns>
        internal Node Child(string name, string source)
        {
            if (!Children.TryGetValue(name, out Node? child))
            {
                child = new Node(source);
                Children[name] = child;
            }

            return child;
        }
    }
}
