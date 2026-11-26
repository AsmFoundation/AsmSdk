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
/// The options of a library's baseline, resolved (the code wins: the assembly attribute's
/// Default* options, then the project's AsmBindings items and properties, then the defaults).
/// </summary>
/// <param name="Namespace">The namespace the baseline lives in (the library's name by default).</param>
/// <param name="Nesting">Files (a class per file, folders as namespaces), Folders (a class per folder) or Flat (everything in Api).</param>
/// <param name="SpanOverloads">Whether pointer-and-length pairs get span overloads.</param>
/// <param name="Members">How the members are declared (static, or instance and maybe virtual).</param>
/// <param name="Sealed">Whether the instance classes are sealed (static classes always are).</param>
/// <param name="Functions">Whether the function classes are written at all (DefaultBindings); the structs always are.</param>
internal sealed record BaselineShape(string Namespace, Nesting Nesting, bool SpanOverloads, MemberShape Members, bool Sealed, bool Functions);

/// <summary>
/// How generated members are declared: their accessibility, and whether they are static methods
/// or instance methods (overridable or not). The baseline's shape comes from the Default*
/// options, an attributed class's from the class itself (a static class gets static members, a
/// non-static partial class instance members).
/// </summary>
/// <param name="Access">The members' accessibility keyword(s): "internal", "public" or "protected internal".</param>
/// <param name="Static">Whether the members are static (every static class) or instance members (ClassKind = Instance).</param>
/// <param name="Virtual">Whether instance members are virtual (a test double or a subclass overrides them); never with Static.</param>
internal sealed record MemberShape(string Access, bool Static, bool Virtual)
{
    /// <summary>
    /// The pack-time placeholders a package's baseline is written with, or null for source
    /// written for one compilation. With them, every modifier the consumer's settings decide is
    /// a NuGet $token$ (see <see cref="Placeholders"/>), and <see cref="Access"/> is the access
    /// token itself.
    /// </summary>
    internal Placeholders? Tokens { get; init; }

    /// <summary>The modifiers of a binding method: "internal static", "public virtual", "protected internal" (or their tokens in a package's baseline).</summary>
    internal string MethodModifiers => Static ? Access + " static" : Tokens?.InstanceMembers ?? Access + (Virtual ? " virtual" : "");

    /// <summary>
    /// The accessibility of a type at namespace level that holds or serves these members: public
    /// for Public and ProtectedInternal (a protected internal member is reached from derived types
    /// in any assembly, so its class and the structs it takes must be public), internal otherwise.
    /// Namespace-level types cannot be protected.
    /// </summary>
    internal string TypeAccess => Tokens?.TypeAccess ?? (Access == "internal" ? "internal" : "public");

    /// <summary>The keyword(s) of an AsmVisibility value: 0 internal, 1 public, 2 protected internal.</summary>
    /// <param name="visibility">The value.</param>
    /// <returns>The accessibility keyword(s).</returns>
    internal static string AccessOf(int visibility) => visibility switch
    {
        1 => "public",
        2 => "protected internal",
        _ => "internal",
    };
}

/// <summary>
/// The NuGet tokens a package's baseline (a .cs.pp in contentFiles) is written with: NuGet
/// replaces each $name$ in the consumer, from the values the package's targets compute out of the
/// consumer's AsmBindings settings (PreprocessorValue items). Every token always has a non-empty
/// value, so no modifier is ever left as an empty token.
/// </summary>
/// <param name="Access">The members' accessibility: internal, public or protected internal.</param>
/// <param name="TypeAccess">The namespace-level types' accessibility: internal or public.</param>
/// <param name="InstanceMembers">An instance member's modifiers: the access, plus virtual when the settings say so.</param>
/// <param name="InstanceClass">An instance class's modifiers before "partial class": the type access, plus sealed when the settings say so.</param>
/// <param name="SpanSymbol">The preprocessor symbol that keeps the span overloads (each is written inside #if of it).</param>
internal sealed record Placeholders(string Access, string TypeAccess, string InstanceMembers, string InstanceClass, string SpanSymbol);

/// <summary>
/// Writes the C# of a bound class's members: one blittable [DllImport] extern per function (with
/// a wrapper when it takes a bool or a string), span overloads, nested classes per file or
/// folder, and the library's structs (sequential C# structs, in its default class). Pure text, no
/// Roslyn: what the generator decided (which functions, which layout) comes in, source comes out.
/// </summary>
/// <remarks>
/// <para>
/// The C types map to the blittable C# types of the same size and signedness
/// (<see cref="BaseType(string, bool)"/>), a STRUCT to its generated struct, fully qualified,
/// so each call is direct, with no runtime marshalling. Two kinds
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
    /// <param name="shape">How the members (and the nested classes, which take its accessibility and static-ness) are declared.</param>
    /// <param name="className">
    /// The bound class's own name. A function named like it cannot be bound in it (C# forbids a
    /// member named like its type, CS0542): reported as a collision. The nested file and folder
    /// classes take the suffix <see cref="ClassSuffix"/> instead (see <see cref="ClassName"/>).
    /// </param>
    /// <param name="collisions">Receives a description of each name generated twice in one scope.</param>
    /// <param name="structs">The library's STRUCTs: each C name with its generated C# type, fully qualified (global::Demo.Native.Rect), which a signature naming the struct uses.</param>
    /// <param name="reserved">
    /// Names already taken in the class's own scope, or null: a method or nested class of that
    /// name would be a duplicate member (CS0102), reported as a collision instead.
    /// </param>
    internal static void WriteMembers(StringBuilder output, int indent, string library, IEnumerable<ApiFunctionModel> functions,
        Nesting nesting, bool spanOverloads, MemberShape shape, string className, List<string> collisions,
        IReadOnlyDictionary<string, string> structs, ISet<string>? reserved = null)
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

        WriteNode(output, indent, library, root, className, spanOverloads, shape, collisions, "", structs, reserved);
    }

    /// <summary>Writes one scope: its methods, then its nested classes.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The members' indentation.</param>
    /// <param name="library">The import name.</param>
    /// <param name="node">The scope.</param>
    /// <param name="scopeName">The class name of the scope (the bound class's for the root).</param>
    /// <param name="spanOverloads">Whether to write span overloads.</param>
    /// <param name="shape">How the members are declared.</param>
    /// <param name="collisions">Receives names generated twice.</param>
    /// <param name="scope">The scope's path, for messages ("" for the class itself).</param>
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    /// <param name="reserved">Names this scope already holds, or null.</param>
    private static void WriteNode(StringBuilder output, int indent, string library, Node node, string scopeName, bool spanOverloads, MemberShape shape,
        List<string> collisions, string scope, IReadOnlyDictionary<string, string> structs, ISet<string>? reserved)
    {
        // Each nested class's name: a file's or folder's class that holds a function of its own
        // name takes the suffix (see ClassName). A suffixed name another class already has would
        // be a duplicate member: reported, and the suffixed class left out.
        var childNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Node> child in node.Children.ToList())
        {
            string name = ClassName(child.Key, child.Value.Functions);
            if (name != child.Key && node.Children.ContainsKey(name))
            {
                collisions.Add($"the file or folder {child.Value.Source} becomes the class {scope}{name}, and so does {node.Children[name].Source}");
                node.Children.Remove(child.Key);
                continue;
            }

            childNames[child.Key] = name;
        }

        // A struct of the default class named like one of its nested classes (STRUCT add and
        // add.asm both make Add) would be a duplicate member: reported, and the class left out.
        foreach (string child in node.Children.Keys.Where(k => reserved?.Contains(childNames[k]) == true).ToList())
        {
            collisions.Add($"the file or folder {node.Children[child].Source} becomes the class {scope}{childNames[child]}, the name of a struct");
            node.Children.Remove(child);
            childNames.Remove(child);
        }

        // Two functions whose PascalCase names meet, or a function named like a nested class,
        // would be a C# error in the generated code; reported instead, at the attribute.
        var methodNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ApiFunctionModel function in node.Functions)
        {
            string method = Pascal(function.Name);
            // A function named like the class that holds it would be CS0542 (C# forbids a member
            // named like its type). A file's or folder's class has taken the suffix for that
            // already, so this is the app's own class, which the generator cannot rename.
            if (method == scopeName)
            {
                collisions.Add($"{function.Name} becomes {scope}{method}, the name of the class that holds it");
                continue;
            }

            if (methodNames.TryGetValue(method, out string? other))
            {
                collisions.Add($"{function.Name} and {other} both become {scope}{method}");
                continue;
            }

            if (childNames.ContainsValue(method))
            {
                collisions.Add($"{function.Name} becomes {scope}{method}, the name of a nested class");
                continue;
            }

            if (reserved?.Contains(method) == true)
            {
                collisions.Add($"{function.Name} becomes {scope}{method}, the name of a struct");
                continue;
            }

            methodNames[method] = function.Name;
            WriteFunction(output, indent, library, function, method, spanOverloads, shape, structs);
        }

        foreach (KeyValuePair<string, Node> child in node.Children)
        {
            // A nested class takes the scope's kind: static members live in static classes, an
            // instance class's file classes are instance classes too (each instantiated alone).
            string name = childNames[child.Key];
            Indent(output, indent).Append("/// <summary>The functions of ").Append(child.Value.Source).Append(".</summary>\n");
            Indent(output, indent).Append(shape.Access).Append(shape.Static ? " static partial class " : " partial class ").Append(name).Append('\n');
            Indent(output, indent).Append("{\n");
            WriteNode(output, indent + 1, library, child.Value, name, spanOverloads, shape, collisions, scope + name + ".", structs, null);
            Indent(output, indent).Append("}\n");
        }
    }

    /// <summary>The name of the baseline's class for functions that have no file class (Flat, or a root file under Folders): a namespace cannot hold methods.</summary>
    internal const string RootClassName = "Api";

    /// <summary>
    /// The suffix a generated class takes when it holds a function of its own name: answer() in
    /// answer.asm is AnswerAsm.Answer (the owner's choice of 2026-11-26, over the method Invoke
    /// the first builds wrote). C# forbids a member named like its type (CS0542), and the
    /// function keeps its name this way.
    /// </summary>
    internal const string ClassSuffix = "Asm";

    /// <summary>
    /// The name a generated class (a file's, a folder's, the baseline's Api) takes: its own, or
    /// with <see cref="ClassSuffix"/> when one of its functions has that name in PascalCase.
    /// </summary>
    /// <param name="name">The class's name from its file or folder (Answer for answer.asm).</param>
    /// <param name="functions">The functions it holds directly.</param>
    /// <returns>"AnswerAsm" when answer() is among them; the name otherwise.</returns>
    /// <remarks>
    /// Only the class that would hold such a member changes; its neighbours keep their names, and
    /// a clash of the suffixed name with another class is reported by the caller as a collision.
    /// </remarks>
    internal static string ClassName(string name, IEnumerable<ApiFunctionModel> functions) =>
        functions.Any(f => Pascal(f.Name) == name) ? name + ClassSuffix : name;

    /// <summary>
    /// The namespace of a library's baseline by default: its import name, each dotted part in
    /// PascalCase ("apiproto" Apiproto, "Demo.Native" Demo.Native, "KvAsm" KvAsm).
    /// </summary>
    /// <param name="library">The library's import name (its AssemblyName).</param>
    /// <returns>The namespace.</returns>
    internal static string LibraryNamespace(string library) =>
        string.Join(".", library.Split('.').Where(part => part.Length > 0).Select(Pascal));

    /// <summary>
    /// Writes a library's baseline, the bindings every consumer has without writing a line (the
    /// owner's spec of 2026-11-25): a namespace named after the library, its STRUCTs at the root,
    /// one class per .asm file with folders as namespaces (Nesting Files, the default), one class
    /// per folder (Folders), or every function in the class Api (Flat; also where Folders puts the
    /// root files).
    /// </summary>
    /// <param name="output">Where to write (a whole compilation unit's namespaces are appended).</param>
    /// <param name="library">The library.</param>
    /// <param name="shape">The baseline's options.</param>
    /// <param name="structs">Every struct's C name and C# type (fully qualified, in the baseline namespace).</param>
    /// <param name="collisions">Receives each name the C# could not hold twice; the class concerned is left out.</param>
    /// <param name="writeStructs">
    /// False leaves the structs out (a package's baseline writes them once, before its layouts);
    /// they still count for the collisions, since they are in the same namespace.
    /// </param>
    /// <remarks>
    /// The classes are declared at namespace level, so their accessibility is public or internal
    /// only (<see cref="MemberShape.TypeAccess"/>); a ProtectedInternal baseline has public classes
    /// whose members are protected internal. The structs are always written, also when the
    /// function classes are not (DefaultBindings false): the attributed classes' copies name them.
    /// Writes nothing at all for a library with neither (the caller then adds no source).
    /// </remarks>
    internal static void WriteBaseline(StringBuilder output, ApiLibraryModel library, BaselineShape shape,
        IReadOnlyDictionary<string, string> structs, List<string> collisions, bool writeStructs = true)
    {
        // namespace -> (class -> (what it stands for, its functions)), the root namespace first.
        var namespaces = new SortedDictionary<string, SortedDictionary<string, (string Source, List<ApiFunctionModel> Functions)>>(StringComparer.Ordinal);
        if (shape.Functions)
        {
            foreach (ApiFunctionModel function in library.Functions)
            {
                string[] path = function.File.Split('/');
                string[] folders = path.Take(path.Length - 1).ToArray();
                (string ns, string cls, string source) = shape.Nesting switch
                {
                    Nesting.Files => (Join(shape.Namespace, folders), Pascal(path[path.Length - 1]), function.File),
                    Nesting.Folders when folders.Length > 0 => (Join(shape.Namespace, folders.Take(folders.Length - 1)), Pascal(folders[folders.Length - 1]), string.Join("/", folders)),
                    _ => (shape.Namespace, RootClassName, "the library " + library.Library),
                };
                if (!namespaces.TryGetValue(ns, out var classes))
                {
                    classes = new SortedDictionary<string, (string, List<ApiFunctionModel>)>(StringComparer.Ordinal);
                    namespaces[ns] = classes;
                }

                if (!classes.TryGetValue(cls, out var entry))
                {
                    entry = (source, new List<ApiFunctionModel>());
                    classes[cls] = entry;
                }

                entry.Functions.Add(function);
            }
        }

        // A class that holds a function of its own name takes the suffix (answer.asm's class is
        // AnswerAsm, api() under Flat makes ApiAsm), before the checks below, which then see the
        // names the classes really get. A suffixed name another class has is a collision.
        foreach (KeyValuePair<string, SortedDictionary<string, (string Source, List<ApiFunctionModel> Functions)>> ns in namespaces)
        {
            foreach (string cls in ns.Value.Keys.ToList())
            {
                (string Source, List<ApiFunctionModel> Functions) entry = ns.Value[cls];
                string name = ClassName(cls, entry.Functions);
                if (name == cls)
                {
                    continue;
                }

                ns.Value.Remove(cls);
                if (ns.Value.TryGetValue(name, out var taken))
                {
                    collisions.Add($"{entry.Source} becomes the class {ns.Key}.{name}, and so does {taken.Source}");
                    continue;
                }

                ns.Value[name] = entry;
            }
        }

        // What C# cannot hold: a class named like a struct of the root namespace, and a class
        // named like a namespace beside it (simd.asm and the folder simd/ both make Simd).
        var structNames = new HashSet<string>(library.Structs.Select(s => Pascal(s.Name)), StringComparer.Ordinal);
        foreach (KeyValuePair<string, SortedDictionary<string, (string Source, List<ApiFunctionModel> Functions)>> ns in namespaces)
        {
            foreach (string cls in ns.Value.Keys.ToList())
            {
                string full = ns.Key + "." + cls;
                if (ns.Key == shape.Namespace && structNames.Contains(cls))
                {
                    collisions.Add($"{ns.Value[cls].Source} becomes the class {full}, the name of a struct");
                    ns.Value.Remove(cls);
                }
                else if (namespaces.Keys.Any(other => other == full || other.StartsWith(full + ".", StringComparison.Ordinal)))
                {
                    collisions.Add($"{ns.Value[cls].Source} becomes the class {full}, and a folder the namespace {full}");
                    ns.Value.Remove(cls);
                }
            }
        }

        if ((!writeStructs || library.Structs.Count == 0) && namespaces.Values.All(classes => classes.Count == 0))
        {
            return;
        }

        // The root namespace first (it holds the structs), then the folders' in order.
        IEnumerable<string> order = new[] { shape.Namespace }.Concat(namespaces.Keys.Where(k => k != shape.Namespace));
        foreach (string ns in order)
        {
            namespaces.TryGetValue(ns, out var classes);
            bool root = ns == shape.Namespace;
            if (!root && (classes is null || classes.Count == 0))
            {
                continue;
            }

            // Without the structs, an empty root namespace says nothing.
            if (root && !writeStructs && (classes is null || classes.Count == 0))
            {
                continue;
            }

            output.Append("namespace ").Append(ns).Append("\n{\n");
            if (root && writeStructs)
            {
                WriteStructs(output, 1, library.Structs, shape.Members.TypeAccess, structs, collisions);
            }

            // A static class is sealed already; an instance class is sealed when the shape says
            // so, or (a package's baseline) when the consumer's settings fill the token so.
            string declaration = shape.Members.Static ? shape.Members.TypeAccess + " static partial class "
                : shape.Members.Tokens is { } tokens ? tokens.InstanceClass + " partial class "
                : shape.Members.TypeAccess + (shape.Sealed ? " sealed partial class " : " partial class ");
            foreach (KeyValuePair<string, (string Source, List<ApiFunctionModel> Functions)> cls in classes ?? new SortedDictionary<string, (string, List<ApiFunctionModel>)>())
            {
                Indent(output, 1).Append("/// <summary>The functions of ").Append(Xml(cls.Value.Source)).Append(" (the asm library ").Append(Xml(library.Library)).Append(").</summary>\n");
                Indent(output, 1).Append(declaration).Append(cls.Key).Append('\n');
                Indent(output, 1).Append("{\n");
                WriteMembers(output, 2, library.Library, cls.Value.Functions, Nesting.Flat, shape.SpanOverloads, shape.Members, cls.Key, collisions, structs);
                Indent(output, 1).Append("}\n");
            }

            output.Append("}\n");
        }
    }

    /// <summary>The layouts a package's baseline holds, in the order of its #if chain: each nesting with a static and an instance variant.</summary>
    internal static readonly Nesting[] PackageLayouts = { Nesting.Files, Nesting.Folders, Nesting.Flat };

    /// <summary>
    /// Writes the baseline an asm package carries for its .NET consumers (the owner's phase-4
    /// spec: written at pack time, contentFiles/cs/net7.0/AsmBindings/&lt;library&gt;.cs.pp): every
    /// layout the settings can choose, each under #if, the namespace and the modifiers as NuGet
    /// tokens. NuGet fills the tokens in each consumer from the values the package's targets
    /// compute out of the consumer's AsmBindings settings, so a consumer of the package gets the
    /// same source a ProjectReference consumer with the same settings gets from
    /// <see cref="WriteBaseline"/>.
    /// </summary>
    /// <param name="output">Where to write (after the file's header comment: the #define lines must precede every token of the file).</param>
    /// <param name="library">The library, from the API file packed with it.</param>
    /// <param name="tokenPrefix">The tokens' prefix, unique to the package ("asmbindings_Test_Calc_"): letters, digits and underscores only, as NuGet's token names are.</param>
    /// <param name="layoutCollisions">Receives, per nesting, each name C# cannot hold twice in that layout (the same for its static and instance variants).</param>
    /// <param name="structCollisions">Receives each field name generated twice in one struct (every layout has them).</param>
    /// <remarks>
    /// <para>
    /// The tokens, each "$" + prefix + suffix + "$": namespace; nesting (Files, Folders, Flat),
    /// classkind (Static, Instance), spanoverloads and functions (true, false), which the
    /// #define lines turn into the file's own symbols; access, typeaccess, members and
    /// classmodifiers (<see cref="Placeholders"/>). AsmWritePackageConsumerTargets fills these
    /// nine and no other: a token left unfilled stays in the C# as it is (NuGet leaves an unknown
    /// token alone, measured), which the consumer's compile then reports.
    /// </para>
    /// <para>
    /// Why #define lines filled by tokens: a #define is local to its file, so the symbols never
    /// reach the consumer's own code or another package's baseline, and the consumer's
    /// DefineConstants stay untouched. The structs come once, before the layouts (they are the
    /// same in each). The span overloads sit inside #if ASMBINDINGS_SPANS_true one by one, and
    /// DefaultBindings false (ASMBINDINGS_FUNCTIONS_false) keeps the structs only.
    /// </para>
    /// <para>
    /// A layout whose names collide keeps an #error with the ASM0143 text the SDK reports for a
    /// ProjectReference: a consumer that chooses it cannot build, as it could not with the
    /// reference. The chain ends with an #error for symbols no layout knows, which only a
    /// consumer that fills the tokens by hand can reach (the package's targets check every
    /// value first, ASM0146).
    /// </para>
    /// <para>Footgun: nothing written here may say the word the switch searches for (see the consumer targets): the baseline would turn the generator on in every consumer.</para>
    /// </remarks>
    internal static void WritePackageBaseline(StringBuilder output, ApiLibraryModel library, string tokenPrefix,
        IDictionary<Nesting, List<string>> layoutCollisions, List<string> structCollisions)
    {
        string Token(string suffix) => "$" + tokenPrefix + suffix + "$";
        output.Append("#define ASMBINDINGS_NESTING_").Append(Token("nesting")).Append('\n');
        output.Append("#define ASMBINDINGS_KIND_").Append(Token("classkind")).Append('\n');
        output.Append("#define ASMBINDINGS_SPANS_").Append(Token("spanoverloads")).Append('\n');
        output.Append("#define ASMBINDINGS_FUNCTIONS_").Append(Token("functions")).Append('\n');

        var tokens = new Placeholders(Token("access"), Token("typeaccess"), Token("members"), Token("classmodifiers"), "ASMBINDINGS_SPANS_true");
        string ns = Token("namespace");
        // Fully qualified through the namespace token: a signature names global::<namespace>.Rect,
        // whatever namespace the consumer gives the baseline.
        var structs = library.Structs.ToDictionary(s => s.Name, s => "global::" + ns + "." + Pascal(s.Name), StringComparer.Ordinal);
        string fix = $"Rename one of them, or lay the baseline out differently (<AsmBindings Include=\"{library.Library}\" Nesting=\"...\" />).";
        if (library.Structs.Count > 0)
        {
            output.Append("namespace ").Append(ns).Append("\n{\n");
            WriteStructs(output, 1, library.Structs, tokens.TypeAccess, structs, structCollisions);
            output.Append("}\n");
            foreach (string collision in structCollisions)
            {
                output.Append("#error ASM0143: In the bindings of ").Append(library.Library).Append(" (namespace ").Append(ns).Append("): ")
                    .Append(collision).Append(". Rename one of the fields.\n");
            }
        }

        if (library.Functions.Count == 0)
        {
            return;
        }

        output.Append("#if ASMBINDINGS_FUNCTIONS_true\n");
        string directive = "#if ";
        foreach (Nesting nesting in PackageLayouts)
        {
            var collisions = new List<string>();
            foreach (bool isStatic in new[] { true, false })
            {
                output.Append(directive).Append("ASMBINDINGS_NESTING_").Append(nesting).Append(" && ASMBINDINGS_KIND_").Append(isStatic ? "Static" : "Instance").Append('\n');
                directive = "#elif ";
                // Virtual and Sealed are no variants: the members and classmodifiers tokens carry
                // them, so the shape's own flags stay off.
                var shape = new BaselineShape(ns, nesting, SpanOverloads: true, new MemberShape(tokens.Access, isStatic, Virtual: false) { Tokens = tokens }, Sealed: false, Functions: true);
                var found = new List<string>();
                WriteBaseline(output, library, shape, structs, found, writeStructs: false);
                foreach (string collision in found)
                {
                    output.Append("#error ASM0143: In the bindings of ").Append(library.Library).Append(" (namespace ").Append(ns).Append("): ")
                        .Append(collision).Append(". ").Append(fix).Append('\n');
                }

                // The instance variant declares the same classes: its collisions are the same.
                if (isStatic)
                {
                    collisions.AddRange(found);
                }
            }

            layoutCollisions[nesting] = collisions;
        }

        output.Append("#else\n");
        output.Append("#error ASM0146: The AsmBindings settings of ").Append(library.Library)
            .Append(" reached its baseline unchecked (Nesting ").Append(Token("nesting")).Append(", ClassKind ").Append(Token("classkind"))
            .Append("): the package's targets compute them; restore and build the project instead of filling the tokens by hand.\n");
        output.Append("#endif\n");
        output.Append("#endif\n");
    }

    /// <summary>A namespace and the PascalCase names of folders under it.</summary>
    /// <param name="root">The namespace.</param>
    /// <param name="folders">The folders, outermost first.</param>
    /// <returns>"Demo.Native.Simd.Avx" for ("Demo.Native", ["simd", "avx"]).</returns>
    private static string Join(string root, IEnumerable<string> folders) =>
        string.Join(".", new[] { root }.Concat(folders.Select(Pascal)));

    /// <summary>
    /// Writes the library's STRUCTs, each a sequential C# struct of the same fields: the
    /// fixed-width types as their C# equivalents, bool as bool (one byte, as in C), char as
    /// byte, pointers and function pointers as they are, a nested struct as its generated
    /// struct, an array as a fixed buffer. Each summary states the C line, the size and every
    /// offset, so the asm code's constants can be compared with what C# sees.
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The structs' indentation.</param>
    /// <param name="library">The library's structs.</param>
    /// <param name="access">"internal" or "public": the default class's accessibility, which the structs share.</param>
    /// <param name="structs">Every struct's C name and C# type (for fields that hold or point at one).</param>
    /// <param name="collisions">Receives each field name generated twice in one struct.</param>
    /// <remarks>
    /// Sequential, not explicit offsets: .NET lays these fields out exactly as C does on every
    /// RID the API covers (measured on win-x64 with a bool, a pointer, fixed buffers, a nested
    /// struct and a function pointer; the tests read every field back through the asm code on
    /// each CI machine), and Sequential keeps doing so where a pointer is 4 bytes. Not partial:
    /// fields added in another part would have no defined order (CS0282) and break the layout.
    /// A field named like its struct (C allows it, C# forbids it: CS0542) is Value.
    /// </remarks>
    internal static void WriteStructs(StringBuilder output, int indent, IEnumerable<ApiStructModel> library, string access,
        IReadOnlyDictionary<string, string> structs, List<string> collisions)
    {
        foreach (ApiStructModel model in library)
        {
            string name = Pascal(model.Name);
            Indent(output, indent).Append("/// <summary><c>STRUCT ").Append(Xml(model.Declaration)).Append("</c>: ")
                .Append(model.Size).Append(" bytes, aligned to ").Append(model.Alignment).Append(".</summary>\n");
            Indent(output, indent).Append('[').Append(Interop).Append(".StructLayout(").Append(Interop).Append(".LayoutKind.Sequential)]\n");
            Indent(output, indent).Append(access).Append(" unsafe struct ").Append(name).Append('\n');
            Indent(output, indent).Append("{\n");
            var fieldNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ApiStructFieldModel field in model.Fields)
            {
                string fieldName = Pascal(field.Name);
                if (fieldName == name)
                {
                    fieldName = "Value";
                }

                if (fieldNames.TryGetValue(fieldName, out string? other))
                {
                    collisions.Add($"the fields {other} and {field.Name} of struct {model.Name} both become {name}.{fieldName}");
                    continue;
                }

                fieldNames[fieldName] = field.Name;
                Indent(output, indent + 1).Append("/// <summary><c>").Append(Xml(CSpelling(field.Type, field.Name, field.Count))).Append("</c>, at offset ")
                    .Append(field.Offset).Append(".</summary>\n");
                Indent(output, indent + 1).Append("public ");
                if (field.Count is int count)
                {
                    // A fixed buffer: the SDK allowed arrays of primitive scalars only, the
                    // element types C# fixed buffers take.
                    output.Append("fixed ").Append(BaseType(field.Type, inCallback: false, structs)).Append(' ').Append(fieldName).Append('[').Append(count).Append("];\n");
                }
                else
                {
                    output.Append(TypeName(field.Type, inCallback: false, structs)).Append(' ').Append(fieldName).Append(";\n");
                }
            }

            Indent(output, indent).Append("}\n\n");
        }
    }

    /// <summary>A field's C declaration, for its summary: "const uint8_t* data", "uint32_t h[8]", "int32_t (*cmp)(int32_t, int32_t)".</summary>
    /// <param name="type">The field's type.</param>
    /// <param name="name">Its C name.</param>
    /// <param name="count">Its array length, or null.</param>
    /// <returns>The declaration.</returns>
    private static string CSpelling(ApiTypeModel type, string? name, int? count)
    {
        if (type.Callback is not null)
        {
            string parameters = type.Callback.Parameters.Count == 0
                ? "void"
                : string.Join(", ", type.Callback.Parameters.Select(p => CSpelling(p.Type, p.Name, null)));
            return CSpelling(type.Callback.Returns, null, null) + " (*" + name + ")(" + parameters + ")";
        }

        string spelled = (type.Const ? "const " : "") + type.Base + new string('*', type.Pointers);
        return (name is null ? spelled : spelled + " " + name) + (count is int n ? "[" + n + "]" : "");
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
    /// <param name="shape">How the method is declared (an instance method always wraps a private static extern).</param>
    /// <param name="structs">The library's STRUCTs and their C# types (for pointers to them).</param>
    /// <remarks>
    /// Why [DllImport] and not [LibraryImport]: source generators never see each other's output,
    /// so the runtime's LibraryImport generator would not implement [LibraryImport] methods this
    /// generator writes (measured: CS8795 on every one). The externs are blittable instead, and
    /// the conversions LibraryImport would have generated are written here: a string through
    /// Utf8StringMarshaller's stack-buffer path (on the stack when short, as LibraryImport does),
    /// a bool as one byte. For a blittable signature LibraryImport emits this same DllImport
    /// (measured with the Crc32C sample), so nothing is lost; no runtime marshalling is used, so
    /// it works under DisableRuntimeMarshalling and in NativeAOT. The static shape keeps that
    /// direct extern (the owner's approved spec: no call in between); an extern cannot be an
    /// instance method, so the instance shape always wraps it.
    /// </remarks>
    private static void WriteFunction(StringBuilder output, int indent, string library, ApiFunctionModel function, string method,
        bool spanOverloads, MemberShape shape, IReadOnlyDictionary<string, string> structs)
    {
        string[] names = function.Parameters.Select((p, i) => ParameterName(p, i)).ToArray();
        bool wrapped = !shape.Static || IsBool(function.Returns) || function.Parameters.Any(p => IsBool(p.Type) || IsString(p.Type));
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

        Indent(output, indent).Append(wrapped ? "private" : shape.Access).Append(" static ").Append(unsafeNative ? "unsafe " : "").Append("extern ")
            .Append(NativeType(function.Returns, structs)).Append(' ').Append(native).Append('(');
        for (int i = 0; i < names.Length; i++)
        {
            output.Append(i == 0 ? "" : ", ").Append(NativeType(function.Parameters[i].Type, structs)).Append(' ').Append(names[i]);
        }

        output.Append(");\n\n");

        if (wrapped)
        {
            WriteWrapper(output, indent, function, method, native, names, shape, structs);
        }

        if (shape.Tokens is { } tokens)
        {
            // A package's baseline: the consumer's settings decide, so the overload is always
            // written, kept or dropped by the file's own #define of the symbol. Only where there
            // is one: an empty #if would only lengthen the file.
            if (SpanPairs(function).Count > 0)
            {
                output.Append("#if ").Append(tokens.SpanSymbol).Append('\n');
                WriteSpanOverload(output, indent, function, method, names, shape, structs);
                output.Append("#endif\n");
            }
        }
        else if (spanOverloads)
        {
            WriteSpanOverload(output, indent, function, method, names, shape, structs);
        }
    }

    /// <summary>
    /// Writes the method that calls a private extern: for a function whose signature has a bool
    /// or a string (each string converted to UTF-8 through Utf8StringMarshaller.ManagedToUnmanagedIn,
    /// a stack buffer of its BufferSize, freed after the call; each bool passed as 1 or 0, a bool
    /// result read as non-zero), and for every function of the instance shape, whose methods
    /// cannot be externs.
    /// </summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The indentation.</param>
    /// <param name="function">The function.</param>
    /// <param name="method">Its C# name.</param>
    /// <param name="native">The private extern's name.</param>
    /// <param name="names">The parameters' C# names.</param>
    /// <param name="shape">How the method is declared: static, or an instance method (virtual when the shape says so).</param>
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    private static void WriteWrapper(StringBuilder output, int indent, ApiFunctionModel function, string method, string native, string[] names, MemberShape shape,
        IReadOnlyDictionary<string, string> structs)
    {
        const string Marshaller = "global::System.Runtime.InteropServices.Marshalling.Utf8StringMarshaller.ManagedToUnmanagedIn";
        int[] strings = Enumerable.Range(0, names.Length).Where(i => IsString(function.Parameters[i].Type)).ToArray();
        bool unsafeWrapper = strings.Length > 0 || HasPointer(function.Returns) || function.Parameters.Any(p => HasPointer(p.Type) && !IsString(p.Type));

        Summary(output, indent, function);
        Indent(output, indent).Append(shape.MethodModifiers).Append(' ').Append(unsafeWrapper ? "unsafe " : "").Append(ReturnType(function.Returns, structs))
            .Append(' ').Append(method).Append('(');
        for (int i = 0; i < names.Length; i++)
        {
            output.Append(i == 0 ? "" : ", ").Append(ParameterType(function.Parameters[i].Type, structs)).Append(' ').Append(names[i]);
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
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    /// <returns>The C# type.</returns>
    private static string NativeType(ApiTypeModel type, IReadOnlyDictionary<string, string> structs) =>
        IsBool(type) ? "byte" : IsString(type) ? "byte*" : TypeName(type, inCallback: false, structs);

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
    /// <param name="shape">How the main method is declared: the overload takes its accessibility and static-ness, never virtual (it calls the main method, so an override of that one covers both).</param>
    /// <param name="structs">The library's STRUCTs and their C# types (a span of structs: const point* pts, size_t count).</param>
    private static void WriteSpanOverload(StringBuilder output, int indent, ApiFunctionModel function, string method, string[] names, MemberShape shape,
        IReadOnlyDictionary<string, string> structs)
    {
        Dictionary<int, int> pairs = SpanPairs(function);
        if (pairs.Count == 0)
        {
            return;
        }

        var lengths = new HashSet<int>(pairs.Values);
        Indent(output, indent).Append("/// <summary><c>").Append(Xml(function.Prototype)).Append("</c>, with spans for the pointer and length pairs.</summary>\n");
        Indent(output, indent).Append(shape.Access).Append(shape.Static ? " static unsafe " : " unsafe ").Append(ReturnType(function.Returns, structs)).Append(' ').Append(method).Append('(');
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
                output.Append(type.Const ? "global::System.ReadOnlySpan<" : "global::System.Span<").Append(BaseType(type, inCallback: false, structs)).Append("> ").Append(names[i]);
            }
            else
            {
                output.Append(ParameterType(type, structs)).Append(' ').Append(names[i]);
            }
        }

        output.Append(")\n");
        Indent(output, indent).Append("{\n");
        int depth = indent + 1;
        foreach (int i in pairs.Keys)
        {
            Indent(output, depth).Append("fixed (").Append(BaseType(function.Parameters[i].Type, inCallback: false, structs)).Append("* __p").Append(i)
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

    /// <summary>
    /// The pointer-and-length pairs of a function's parameters, by the span convention (see
    /// <see cref="WriteSpanOverload"/>): each pointer's index with its length's index. Empty when the
    /// function gets no span overload, which a package's baseline uses to skip an empty #if.
    /// </summary>
    /// <param name="function">The function.</param>
    /// <returns>Pointer index to length index; a parameter is in at most one pair.</returns>
    private static Dictionary<int, int> SpanPairs(ApiFunctionModel function)
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
                // The length is taken: it cannot start the next pair.
                i++;
            }
        }

        return pairs;
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
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    /// <returns>The C# type.</returns>
    private static string ParameterType(ApiTypeModel type, IReadOnlyDictionary<string, string> structs) =>
        IsString(type) ? "string" : TypeName(type, inCallback: false, structs);

    /// <summary>A return type: a const char* stays byte* (who frees it is the library's rule).</summary>
    /// <param name="type">The type.</param>
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    /// <returns>The C# type.</returns>
    private static string ReturnType(ApiTypeModel type, IReadOnlyDictionary<string, string> structs) => TypeName(type, inCallback: false, structs);

    /// <summary>The C# spelling of a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="inCallback">Inside a function pointer's signature, where nothing marshals (bool is byte).</param>
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    /// <returns>The C# type.</returns>
    private static string TypeName(ApiTypeModel type, bool inCallback, IReadOnlyDictionary<string, string> structs)
    {
        if (type.Callback is not null)
        {
            var parts = type.Callback.Parameters.Select(p => TypeName(p.Type, inCallback: true, structs)).ToList();
            parts.Add(TypeName(type.Callback.Returns, inCallback: true, structs));
            return "delegate* unmanaged<" + string.Join(", ", parts) + ">";
        }

        return BaseType(type, inCallback, structs) + new string('*', type.Pointers);
    }

    /// <summary>The C# type of a type's base: the generated struct for a STRUCT, the C# primitive for a C type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="inCallback">Inside a function pointer's signature: bool is byte there.</param>
    /// <param name="structs">The library's STRUCTs and their C# types.</param>
    /// <returns>The C# type, without pointers.</returns>
    /// <exception cref="ArgumentException">A STRUCT the library does not declare (an API file the SDK did not write), or an unknown C type.</exception>
    private static string BaseType(ApiTypeModel type, bool inCallback, IReadOnlyDictionary<string, string> structs)
    {
        if (!type.IsStruct)
        {
            return BaseType(type.Base, inCallback);
        }

        return structs.TryGetValue(type.Base, out string? name)
            ? name
            : throw new ArgumentException($"the struct '{type.Base}' is not declared in the API file", nameof(type));
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
