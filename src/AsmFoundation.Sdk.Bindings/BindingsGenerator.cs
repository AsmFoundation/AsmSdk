using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AsmFoundation.Sdk.Bindings;

/// <summary>
/// The C# bindings of a project's asm libraries: blittable [DllImport] declarations (and the
/// wrappers that convert bools and strings) from their API files (the PROTO lines), laid out by
/// the app's [AsmLibrary] attributes. Not [LibraryImport]: generators never see each other's
/// output, so the runtime's LibraryImport generator would leave such declarations unimplemented
/// (measured: CS8795); see CSharpEmitter.
/// </summary>
/// <remarks>
/// <para>
/// Inputs: the API files (AdditionalFiles with AsmApiFile="true", which the SDK's consumer
/// targets add for each asm reference), the project's RootNamespace, [assembly: AsmLibrary]
/// (options for every library, then per library) and [AsmLibrary] on static partial classes
/// (where bindings go). Output: one source per bound class.
/// </para>
/// <para>
/// The owner's rules (catalog asmsdk/plans/csharp-bindings.md, approved 2026-11-23): a class named
/// after each library binds what no attributed class takes, nested by file with folders as levels;
/// an attributed class binds the whole library, chosen Files, or a Folder; each function is bound
/// once unless a class says AliasBinding; spans by the naming convention; every option inherits
/// from the level above when not written (the attribute's named arguments say what was written,
/// since an attribute cannot hold "unset"). A class that selects Files or a Folder is that
/// selection's binding, so its default layout is Flat.
/// </para>
/// <para>
/// Structs (phase 3, the API file's "structs"): a sequential C# struct per STRUCT line, in the
/// library's default class, which is therefore generated whenever the library has structs, even
/// when attributed classes took every function. Every signature names a struct fully qualified,
/// so a binding in any class reaches it; a public class binding a function that takes a struct
/// of an internal default class is ASM0145 (it would be CS0051 in the generated code).
/// </para>
/// <para>
/// Incremental: the API files are read once per change of their text; attributed classes come
/// from ForAttributeWithMetadataName; the assembly's attributes are read from the compilation
/// (a cheap step whose equatable output stops the pipeline when nothing changed).
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class BindingsGenerator : IIncrementalGenerator
{
    /// <summary>The attribute's metadata name.</summary>
    internal const string AttributeName = "AsmFoundation.Interop.AsmLibraryAttribute";

    /// <summary>ASM0140: an attribute names no referenced asm library.</summary>
    internal static readonly DiagnosticDescriptor UnknownLibrary = new(
        "ASM0140", "Unknown asm library",
        "[AsmLibrary(\"{0}\")] names no asm library this project references with PROTO lines; it references {1}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0141: a Files or Folder entry is not one of the library's.</summary>
    internal static readonly DiagnosticDescriptor UnknownFile = new(
        "ASM0141", "Unknown asm file or folder",
        "[AsmLibrary(\"{0}\")]: {1} is not a file or folder of {0} that has PROTO lines; its files are {2}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0142: a function selected by a second class without AliasBinding.</summary>
    internal static readonly DiagnosticDescriptor BoundTwice = new(
        "ASM0142", "Function bound twice",
        "{0} is already bound by {1}: each function is bound once, unless this class says AliasBinding = true",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0143: a class that cannot hold bindings, or a name generated twice.</summary>
    internal static readonly DiagnosticDescriptor BadClass = new(
        "ASM0143", "Cannot generate the bindings here",
        "{0}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0144: an API file that cannot be read.</summary>
    internal static readonly DiagnosticDescriptor UnreadableApi = new(
        "ASM0144", "Unreadable API file",
        "Cannot read the API file {0}: {1}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>
    /// ASM0145: a public class binds a function whose signature names a struct that the library's
    /// default class keeps internal. The structs live in the default class (the approved spec), so
    /// they are as visible as it is; the compiler would otherwise report CS0051 inside generated
    /// code, which names neither the attribute nor the fix.
    /// </summary>
    internal static readonly DiagnosticDescriptor InternalStruct = new(
        "ASM0145", "Public binding of an internal struct",
        "{0} is public, but {1} takes the struct {2} of {3}, which its default class {4} keeps internal: make {3}'s bindings public ([assembly: AsmLibrary(\"{3}\", Visibility = AsmVisibility.Public)]), or this class internal",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>
    /// The attribute and its enums, added to every compilation that runs the generator. Internal,
    /// so two projects never clash, and conditional, so it stays out of the compiled assembly:
    /// it is read from the source, never at run time.
    /// </summary>
    internal const string AttributeSource = """
        // <auto-generated/> by AsmFoundation.Sdk's bindings generator.
        // Its own nullable context: the string? members would warn (CS8632) in a project that
        // has nullable reference types off.
        #nullable enable
        namespace AsmFoundation.Interop
        {
            /// <summary>How a class lays out the functions it binds.</summary>
            internal enum AsmNesting
            {
                /// <summary>A nested class per .asm file, folders as levels (the default class's default).</summary>
                Files = 0,

                /// <summary>A nested class per folder; a folder's files share it.</summary>
                Folders = 1,

                /// <summary>Every function directly in the class (the default of a class that selects Files or a Folder).</summary>
                Flat = 2,
            }

            /// <summary>The accessibility of the generated members and nested classes.</summary>
            internal enum AsmVisibility
            {
                /// <summary>internal (the default).</summary>
                Internal = 0,

                /// <summary>public.</summary>
                Public = 1,
            }

            /// <summary>
            /// Binds an asm library's functions (its PROTO lines). On the assembly: options for every
            /// library (no name) or for one (its name), and that library's default class. On a static
            /// partial class: the functions it binds (the whole library, Files, or a Folder), which then
            /// leave the default class. A written option overrides the level above.
            /// </summary>
            [global::System.AttributeUsage(global::System.AttributeTargets.Assembly | global::System.AttributeTargets.Class, AllowMultiple = true)]
            [global::System.Diagnostics.Conditional("ASMFOUNDATION_BINDINGS_ATTRIBUTES")]
            internal sealed class AsmLibraryAttribute : global::System.Attribute
            {
                /// <summary>Options for every library (assembly level only).</summary>
                public AsmLibraryAttribute() { }

                /// <summary>Options for, or a class of, one library.</summary>
                /// <param name="library">The library's import name (its AssemblyName).</param>
                public AsmLibraryAttribute(string library) => Library = library;

                /// <summary>The library's import name; null for every library.</summary>
                public string? Library { get; }

                /// <summary>The layout of the bound functions.</summary>
                public AsmNesting Nesting { get; set; }

                /// <summary>Whether a pointer followed by its length (len, length, size, count) also gets a span overload; true by default.</summary>
                public bool SpanOverloads { get; set; }

                /// <summary>The accessibility of the generated members.</summary>
                public AsmVisibility Visibility { get; set; }

                /// <summary>The default class's name (assembly level, per library); the library's name in PascalCase by default.</summary>
                public string? ClassName { get; set; }

                /// <summary>The .asm or .s files this class binds (paths relative to the asm project; the RID qualifier and extension may be left out).</summary>
                public string[]? Files { get; set; }

                /// <summary>The folder whose files this class binds, with its subfolders.</summary>
                public string? Folder { get; set; }

                /// <summary>Lets this class bind functions another class binds too (a copy).</summary>
                public bool AliasBinding { get; set; }
            }
        }
        """;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(output => output.AddSource("AsmFoundation.Interop.AsmLibraryAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8)));

        IncrementalValueProvider<ImmutableArray<ApiFile>> apiFiles = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(pair => pair.Right.GetOptions(pair.Left).TryGetValue("build_metadata.AdditionalFiles.AsmApiFile", out string? flag)
                && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            .Select((pair, cancellation) => ApiFile.Read(pair.Left, cancellation))
            .Collect();

        IncrementalValueProvider<string> rootNamespace = context.AnalyzerConfigOptionsProvider
            .Select((options, _) => options.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? ns) ? ns : "");

        IncrementalValueProvider<EquatableArray<AttributeRequest>> assemblyAttributes = context.CompilationProvider
            .Select((compilation, _) => new EquatableArray<AttributeRequest>(compilation.Assembly.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == AttributeName)
                .Select(AttributeRequest.From)
                .ToArray()));

        IncrementalValueProvider<ImmutableArray<ClassRequest>> classes = context.SyntaxProvider
            .ForAttributeWithMetadataName(AttributeName, (node, _) => node is ClassDeclarationSyntax, (syntax, _) => ClassRequest.From(syntax))
            .Collect();

        context.RegisterSourceOutput(apiFiles.Combine(rootNamespace).Combine(assemblyAttributes).Combine(classes),
            (output, inputs) => Generate(output, inputs.Left.Left.Left, inputs.Left.Left.Right, inputs.Left.Right, inputs.Right));
    }

    /// <summary>Decides who binds what, reports what is wrong, and writes the sources.</summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="files">The API files.</param>
    /// <param name="rootNamespace">The project's RootNamespace (the default classes' namespace).</param>
    /// <param name="assembly">The [assembly: AsmLibrary] attributes.</param>
    /// <param name="classes">The [AsmLibrary] classes.</param>
    private static void Generate(SourceProductionContext output, ImmutableArray<ApiFile> files, string rootNamespace,
        EquatableArray<AttributeRequest> assembly, ImmutableArray<ClassRequest> classes)
    {
        var libraries = new Dictionary<string, ApiLibraryModel>(StringComparer.Ordinal);
        foreach (ApiFile file in files)
        {
            if (file.Library is null)
            {
                output.ReportDiagnostic(Diagnostic.Create(UnreadableApi, Location.None, file.Path, file.Error));
            }
            else if (!libraries.ContainsKey(file.Library.Library))
            {
                // One library may come through several references (a RID-specific and a
                // portable build); its files agree (the SDK checked every RID), so the first.
                libraries[file.Library.Library] = file.Library;
            }
        }

        AttributeRequest? global = assembly.FirstOrDefault(a => a.Library is null);
        // Classes in a stable order (file, then position), so "the second class" of ASM0142 and
        // the generated text never depend on the order the compiler happened to visit them.
        ClassRequest[] ordered = classes.OrderBy(c => c.Location.Path, StringComparer.Ordinal).ThenBy(c => c.Location.Span.Start).ToArray();
        var classMembers = new Dictionary<ClassRequest, List<(AttributeRequest Attribute, List<ApiFunctionModel> Functions)>>();

        foreach (ClassRequest request in ordered)
        {
            if (request.Problem is not null)
            {
                output.ReportDiagnostic(Diagnostic.Create(BadClass, request.Location.ToLocation(), request.Problem));
                continue;
            }

            classMembers[request] = new List<(AttributeRequest, List<ApiFunctionModel>)>();
            foreach (AttributeRequest attribute in request.Attributes)
            {
                if (attribute.Library is null || !libraries.ContainsKey(attribute.Library))
                {
                    string known = libraries.Count == 0 ? "none" : string.Join(", ", libraries.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    output.ReportDiagnostic(Diagnostic.Create(UnknownLibrary, attribute.Location.ToLocation(), attribute.Library ?? "", known));
                }
            }
        }

        // Each library's structs, by C name, as the C# type every signature names them by: nested
        // in the library's default class, fully qualified, so a binding in any class or
        // namespace of the app reaches them.
        var structTypes = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (ApiLibraryModel library in libraries.Values)
        {
            AttributeRequest? perLibrary = assembly.FirstOrDefault(a => a.Library == library.Library);
            string container = "global::" + (rootNamespace.Length > 0 ? rootNamespace + "." : "") + DefaultClassName(library, perLibrary) + ".";
            structTypes[library.Library] = library.Structs.ToDictionary(s => s.Name, s => container + CSharpEmitter.Pascal(s.Name), StringComparer.Ordinal);
        }

        foreach (ApiLibraryModel library in libraries.Values.OrderBy(l => l.Library, StringComparer.Ordinal))
        {
            AttributeRequest? perLibrary = assembly.FirstOrDefault(a => a.Library == library.Library);
            var claimedBy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ClassRequest request in ordered.Where(classMembers.ContainsKey))
            {
                foreach (AttributeRequest attribute in request.Attributes.Where(a => a.Library == library.Library))
                {
                    List<ApiFunctionModel>? selected = Select(output, library, attribute);
                    if (selected is null)
                    {
                        continue;
                    }

                    var bound = new List<ApiFunctionModel>();
                    foreach (ApiFunctionModel function in selected)
                    {
                        if (attribute.AliasBinding == true)
                        {
                            bound.Add(function);
                        }
                        else if (claimedBy.TryGetValue(function.Name, out string? owner))
                        {
                            output.ReportDiagnostic(Diagnostic.Create(BoundTwice, attribute.Location.ToLocation(), function.Name, owner));
                        }
                        else
                        {
                            claimedBy[function.Name] = request.FullName;
                            bound.Add(function);
                        }
                    }

                    classMembers[request].Add((attribute, bound));
                }
            }

            ApiFunctionModel[] rest = library.Functions.Where(f => !claimedBy.ContainsKey(f.Name)).ToArray();
            // The structs live in the default class, so it exists whenever the library has any,
            // even when attributed classes took every function.
            if (rest.Length > 0 || library.Structs.Count > 0)
            {
                WriteDefaultClass(output, library, rest, rootNamespace, global, perLibrary, structTypes[library.Library]);
            }
        }

        foreach (KeyValuePair<ClassRequest, List<(AttributeRequest Attribute, List<ApiFunctionModel> Functions)>> entry in classMembers)
        {
            if (entry.Value.Count > 0)
            {
                WriteClass(output, entry.Key, entry.Value, libraries, global, assembly, structTypes);
            }
        }
    }

    /// <summary>The name of a library's default class: ClassName on the assembly's per-library attribute, or the library's name in PascalCase.</summary>
    /// <param name="library">The library.</param>
    /// <param name="perLibrary">The assembly's options for it, or null.</param>
    /// <returns>The class name.</returns>
    private static string DefaultClassName(ApiLibraryModel library, AttributeRequest? perLibrary) =>
        perLibrary?.ClassName ?? CSharpEmitter.Pascal(library.Library);

    /// <summary>The first struct a function's signature names (behind pointers, in callbacks too), or null.</summary>
    /// <param name="function">The function.</param>
    /// <returns>The struct's C name, or null.</returns>
    private static string? StructIn(ApiFunctionModel function) =>
        new[] { function.Returns }.Concat(function.Parameters.Select(p => p.Type)).Select(StructIn).FirstOrDefault(s => s is not null);

    /// <summary>The first struct a type names, or null.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The struct's C name, or null.</returns>
    private static string? StructIn(ApiTypeModel type)
    {
        if (type.Callback is not null)
        {
            return new[] { type.Callback.Returns }.Concat(type.Callback.Parameters.Select(p => p.Type)).Select(StructIn).FirstOrDefault(s => s is not null);
        }

        return type.IsStruct ? type.Base : null;
    }

    /// <summary>The functions an attribute selects: all, its Files, or its Folder; null (after ASM0141) when an entry matches nothing.</summary>
    /// <param name="output">The generator's output, for ASM0141.</param>
    /// <param name="library">The library.</param>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The functions, or null.</returns>
    private static List<ApiFunctionModel>? Select(SourceProductionContext output, ApiLibraryModel library, AttributeRequest attribute)
    {
        string knownFiles = string.Join(", ", library.Functions.Select(f => f.File).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal));
        if (attribute.Files is { } files)
        {
            var result = new List<ApiFunctionModel>();
            bool ok = true;
            foreach (string entry in files)
            {
                ApiFunctionModel[] matched = library.Functions.Where(f => FileMatches(entry, f.File)).ToArray();
                if (matched.Length == 0)
                {
                    output.ReportDiagnostic(Diagnostic.Create(UnknownFile, attribute.Location.ToLocation(), library.Library, entry, knownFiles));
                    ok = false;
                }

                result.AddRange(matched.Where(m => !result.Contains(m)));
            }

            return ok ? result : null;
        }

        if (attribute.Folder is { } folder)
        {
            string prefix = folder.Replace('\\', '/').Trim('/') + "/";
            List<ApiFunctionModel> result = library.Functions.Where(f => f.File.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (result.Count == 0)
            {
                output.ReportDiagnostic(Diagnostic.Create(UnknownFile, attribute.Location.ToLocation(), library.Library, folder, knownFiles));
                return null;
            }

            return result;
        }

        return library.Functions.ToList();
    }

    /// <summary>
    /// Whether a Files entry names a logical file: as written, or without its extension, or also
    /// without its RID qualifier ("simd/sum.asm", "simd/sum.win.asm" and "simd/sum" all name
    /// "simd/sum"); case-insensitive, as Windows and macOS file systems are.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="file">The logical file.</param>
    /// <returns>True when it names it.</returns>
    private static bool FileMatches(string entry, string file)
    {
        string candidate = entry.Replace('\\', '/').Trim();
        for (int i = 0; i < 3; i++)
        {
            if (string.Equals(candidate, file, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int dot = candidate.LastIndexOf('.');
            if (dot <= candidate.LastIndexOf('/'))
            {
                return false;
            }

            candidate = candidate.Substring(0, dot);
        }

        return false;
    }

    /// <summary>Writes a library's default class: its structs, then what no attributed class took.</summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="library">The library.</param>
    /// <param name="functions">The functions left for it (may be none, when only structs remain).</param>
    /// <param name="rootNamespace">Its namespace.</param>
    /// <param name="global">The assembly's options for every library, or null.</param>
    /// <param name="perLibrary">The assembly's options for this library, or null.</param>
    /// <param name="structs">The library's structs and their C# types.</param>
    private static void WriteDefaultClass(SourceProductionContext output, ApiLibraryModel library, ApiFunctionModel[] functions, string rootNamespace,
        AttributeRequest? global, AttributeRequest? perLibrary, IReadOnlyDictionary<string, string> structs)
    {
        Nesting nesting = (Nesting)(perLibrary?.Nesting ?? global?.Nesting ?? (int)Nesting.Files);
        bool spans = perLibrary?.SpanOverloads ?? global?.SpanOverloads ?? true;
        string access = (perLibrary?.Visibility ?? global?.Visibility ?? 0) == 1 ? "public" : "internal";
        string className = DefaultClassName(library, perLibrary);

        var text = new StringBuilder();
        text.Append("// <auto-generated/> by AsmFoundation.Sdk's bindings generator, from ").Append(System.IO.Path.GetFileName(library.Path)).Append(".\n");
        bool hasNamespace = rootNamespace.Length > 0;
        if (hasNamespace)
        {
            text.Append("namespace ").Append(rootNamespace).Append("\n{\n");
        }

        int indent = hasNamespace ? 1 : 0;
        CSharpEmitter.Indent(text, indent).Append("/// <summary>The functions and structs of the asm library ").Append(library.Library).Append(" (its PROTO and STRUCT lines).</summary>\n");
        CSharpEmitter.Indent(text, indent).Append(access).Append(" static partial class ").Append(className).Append('\n');
        CSharpEmitter.Indent(text, indent).Append("{\n");
        var collisions = new List<string>();
        CSharpEmitter.WriteStructs(text, indent + 1, library.Structs, access, structs, collisions);
        // The structs' names are members of this class: a nested class or method of the same name
        // is reported rather than generated.
        var reserved = new HashSet<string>(library.Structs.Select(s => CSharpEmitter.Pascal(s.Name)), StringComparer.Ordinal);
        CSharpEmitter.WriteMembers(text, indent + 1, library.Library, functions, nesting, spans, access, className, collisions, structs, reserved);
        CSharpEmitter.Indent(text, indent).Append("}\n");
        if (hasNamespace)
        {
            text.Append("}\n");
        }

        foreach (string collision in collisions)
        {
            output.ReportDiagnostic(Diagnostic.Create(BadClass, Location.None, $"In {className}, the binding of {library.Library}: {collision}. Bind one of them in a class of its own ([AsmLibrary(Files = ...)]), or nest by file."));
        }

        output.AddSource($"AsmBindings.{Sanitize(rootNamespace)}.{className}.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
    }

    /// <summary>Writes the members an attributed class binds, into its partial declaration.</summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="request">The class.</param>
    /// <param name="bindings">Each attribute and the functions it bound.</param>
    /// <param name="libraries">The libraries, by name.</param>
    /// <param name="global">The assembly's options for every library, or null.</param>
    /// <param name="assembly">Every assembly attribute (for the per-library options).</param>
    /// <param name="structTypes">Each library's structs and their C# types, by library.</param>
    private static void WriteClass(SourceProductionContext output, ClassRequest request, List<(AttributeRequest Attribute, List<ApiFunctionModel> Functions)> bindings,
        Dictionary<string, ApiLibraryModel> libraries, AttributeRequest? global, EquatableArray<AttributeRequest> assembly,
        Dictionary<string, IReadOnlyDictionary<string, string>> structTypes)
    {
        var text = new StringBuilder();
        text.Append("// <auto-generated/> by AsmFoundation.Sdk's bindings generator, for [AsmLibrary] on ").Append(request.FullName).Append(".\n");
        bool hasNamespace = request.Namespace.Length > 0;
        if (hasNamespace)
        {
            text.Append("namespace ").Append(request.Namespace).Append("\n{\n");
        }

        int indent = hasNamespace ? 1 : 0;
        foreach (string container in request.Containers)
        {
            CSharpEmitter.Indent(text, indent).Append(container).Append('\n');
            CSharpEmitter.Indent(text, indent).Append("{\n");
            indent++;
        }

        CSharpEmitter.Indent(text, indent).Append("static partial class ").Append(request.Name).Append('\n');
        CSharpEmitter.Indent(text, indent).Append("{\n");
        var collisions = new List<string>();
        foreach ((AttributeRequest attribute, List<ApiFunctionModel> functions) in bindings)
        {
            AttributeRequest? perLibrary = assembly.FirstOrDefault(a => a.Library == attribute.Library);
            bool selects = attribute.Files is not null || attribute.Folder is not null;
            Nesting nesting = (Nesting)(attribute.Nesting ?? (selects ? (int)Nesting.Flat : perLibrary?.Nesting ?? global?.Nesting ?? (int)Nesting.Files));
            bool spans = attribute.SpanOverloads ?? perLibrary?.SpanOverloads ?? global?.SpanOverloads ?? true;
            string access = (attribute.Visibility ?? perLibrary?.Visibility ?? global?.Visibility ?? 0) == 1 ? "public" : "internal";
            bool structsPublic = (perLibrary?.Visibility ?? global?.Visibility ?? 0) == 1;
            if (access == "public" && !structsPublic)
            {
                // A public method taking an internal struct is CS0051 in the generated code:
                // reported here, at the attribute, with the fix. Checked once per attribute,
                // at its first such function.
                foreach (ApiFunctionModel function in functions)
                {
                    if (StructIn(function) is string structName)
                    {
                        output.ReportDiagnostic(Diagnostic.Create(InternalStruct, attribute.Location.ToLocation(), request.FullName, function.Name, structName,
                            attribute.Library, DefaultClassName(libraries[attribute.Library!], perLibrary)));
                        break;
                    }
                }
            }

            CSharpEmitter.WriteMembers(text, indent + 1, attribute.Library!, functions, nesting, spans, access, request.Name, collisions, structTypes[attribute.Library!]);
        }

        CSharpEmitter.Indent(text, indent).Append("}\n");
        for (int i = request.Containers.Count - 1; i >= 0; i--)
        {
            indent--;
            CSharpEmitter.Indent(text, indent).Append("}\n");
        }

        if (hasNamespace)
        {
            text.Append("}\n");
        }

        foreach (string collision in collisions)
        {
            output.ReportDiagnostic(Diagnostic.Create(BadClass, request.Location.ToLocation(), $"In {request.FullName}: {collision}. Bind one of them in another class, or nest by file."));
        }

        output.AddSource($"AsmBindings.{Sanitize(request.FullName)}.g.cs", SourceText.From(text.ToString(), Encoding.UTF8));
    }

    /// <summary>A name safe in a generated file's hint name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Letters, digits, dots and underscores only.</returns>
    private static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_').ToArray());
}

/// <summary>An API file, read, or the reason it could not be.</summary>
/// <param name="Path">The file.</param>
/// <param name="Library">The library, or null.</param>
/// <param name="Error">Why it could not be read, or null.</param>
internal sealed record ApiFile(string Path, ApiLibraryModel? Library, string? Error)
{
    /// <summary>Reads an AdditionalFiles item.</summary>
    /// <param name="text">The item.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The file, read or not.</returns>
    internal static ApiFile Read(AdditionalText text, System.Threading.CancellationToken cancellation)
    {
        SourceText? content = text.GetText(cancellation);
        if (content is null)
        {
            return new ApiFile(text.Path, null, "the file cannot be read");
        }

        try
        {
            return new ApiFile(text.Path, ApiLibraryModel.Parse(content.ToString(), text.Path), null);
        }
        catch (FormatException ex)
        {
            return new ApiFile(text.Path, null, ex.Message);
        }
    }
}

/// <summary>A source location kept as values (Location itself does not compare by value).</summary>
/// <param name="Path">The file.</param>
/// <param name="Span">The span.</param>
/// <param name="LineSpan">The lines.</param>
internal sealed record LocationInfo(string Path, TextSpan Span, LinePositionSpan LineSpan)
{
    /// <summary>No location (for diagnostics without one).</summary>
    internal static readonly LocationInfo None = new("", default, default);

    /// <summary>Keeps a syntax node's location.</summary>
    /// <param name="location">The location.</param>
    /// <returns>Its values.</returns>
    internal static LocationInfo From(Location? location) =>
        location is null || location.SourceTree is null
            ? None
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    /// <summary>The location again.</summary>
    /// <returns>A Location, or Location.None.</returns>
    internal Location ToLocation() => Path.Length == 0 ? Location.None : Location.Create(Path, Span, LineSpan);
}

/// <summary>One [AsmLibrary] attribute: its library and the options it wrote (null: not written).</summary>
/// <param name="Library">The library's name, or null (assembly level: every library).</param>
/// <param name="Nesting">AsmNesting as an int, when written.</param>
/// <param name="SpanOverloads">When written.</param>
/// <param name="Visibility">AsmVisibility as an int, when written.</param>
/// <param name="ClassName">When written.</param>
/// <param name="Files">When written.</param>
/// <param name="Folder">When written.</param>
/// <param name="AliasBinding">When written.</param>
/// <param name="Location">The attribute's location.</param>
internal sealed record AttributeRequest(string? Library, int? Nesting, bool? SpanOverloads, int? Visibility, string? ClassName,
    EquatableArray<string>? Files, string? Folder, bool? AliasBinding, LocationInfo Location)
{
    /// <summary>Reads an attribute: its constructor argument and only the named arguments written.</summary>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The request.</returns>
    internal static AttributeRequest From(AttributeData attribute)
    {
        string? library = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        int? nesting = null;
        bool? spans = null;
        int? visibility = null;
        string? className = null;
        EquatableArray<string>? files = null;
        string? folder = null;
        bool? alias = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            switch (argument.Key)
            {
                case "Nesting": nesting = argument.Value.Value is int n ? n : null; break;
                case "SpanOverloads": spans = argument.Value.Value is bool s ? s : null; break;
                case "Visibility": visibility = argument.Value.Value is int v ? v : null; break;
                case "ClassName": className = argument.Value.Value as string; break;
                case "Files":
                    files = argument.Value.IsNull ? null : new EquatableArray<string>(argument.Value.Values.Select(v => v.Value as string ?? "").ToArray());
                    break;
                case "Folder": folder = argument.Value.Value as string; break;
                case "AliasBinding": alias = argument.Value.Value is bool a ? a : null; break;
            }
        }

        return new AttributeRequest(library, nesting, spans, visibility, className, files, folder, alias,
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()));
    }
}

/// <summary>A class with [AsmLibrary]: where its partial declaration goes, and what it asks for.</summary>
/// <param name="Namespace">Its namespace ("" for the global one).</param>
/// <param name="Containers">The declarations of the classes it is nested in, outermost first ("partial class Outer").</param>
/// <param name="Name">Its name.</param>
/// <param name="FullName">Its full name, for messages and file names.</param>
/// <param name="Attributes">Its [AsmLibrary] attributes.</param>
/// <param name="Problem">Why bindings cannot go into it, or null.</param>
/// <param name="Location">Its location.</param>
internal sealed record ClassRequest(string Namespace, EquatableArray<string> Containers, string Name, string FullName,
    EquatableArray<AttributeRequest> Attributes, string? Problem, LocationInfo Location)
{
    /// <summary>Reads a class that ForAttributeWithMetadataName found.</summary>
    /// <param name="context">The attribute's syntax context.</param>
    /// <returns>The request.</returns>
    internal static ClassRequest From(GeneratorAttributeSyntaxContext context)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var syntax = (ClassDeclarationSyntax)context.TargetNode;
        string? problem = null;
        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword) || !symbol.IsStatic)
        {
            problem = $"[AsmLibrary] on {symbol.ToDisplayString()}: the class must be static and partial, so that the generated members can join it";
        }

        var containers = new List<string>();
        for (INamedTypeSymbol? outer = symbol.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            bool partial = outer.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().Any(t => t.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!partial || outer.TypeKind != TypeKind.Class || outer.IsGenericType)
            {
                problem ??= $"[AsmLibrary] on {symbol.ToDisplayString()}: the classes it is nested in must be partial, non-generic classes too";
            }

            containers.Insert(0, (outer.IsStatic ? "static " : "") + "partial class " + outer.Name);
        }

        string ns = symbol.ContainingNamespace is { IsGlobalNamespace: false } containing ? containing.ToDisplayString() : "";
        return new ClassRequest(ns, new EquatableArray<string>(containers.ToArray()), symbol.Name, symbol.ToDisplayString(),
            new EquatableArray<AttributeRequest>(context.Attributes.Select(AttributeRequest.From).ToArray()), problem,
            LocationInfo.From(syntax.Identifier.GetLocation()));
    }
}
