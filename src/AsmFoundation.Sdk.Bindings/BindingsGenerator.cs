using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace AsmFoundation.Sdk.Bindings;

/// <summary>
/// The C# bindings of a project's asm libraries: blittable [DllImport] declarations (and the
/// wrappers that convert bools and strings) from their API files (the PROTO and STRUCT lines).
/// Two sets, as the owner set them out on 2026-11-25: every library's baseline, in a namespace
/// named after it, and the copies the app's [AsmLibrary] classes ask for. Not [LibraryImport]:
/// generators never see each other's output, so the runtime's LibraryImport generator would leave
/// such declarations unimplemented (measured: CS8795); see CSharpEmitter.
/// </summary>
/// <remarks>
/// <para>
/// Inputs: the API files (AdditionalFiles with AsmApiFile="true", with the project's per-library
/// AsmBindings item metadata), the project's AsmBindings* properties (an environment variable of
/// the same name arrives as one), the C# language version, [assembly: AsmLibrary] (for every
/// library, then per library) and [AsmLibrary] on partial classes.
/// </para>
/// <para>
/// The baseline (catalog asmsdk/plans/csharp-bindings.md, phase 4): every function and STRUCT of
/// the library, in the namespace named after it (Demo.Native.Add.AddInts), one class per .asm
/// file with folders as namespaces, the class Api for what has no file class. Shaped by the
/// assembly attribute's Default* options, then the per-library AsmBindings item, then the
/// AsmBindings* properties: the code wins. DefaultBindings = false leaves the function classes
/// out (the structs stay, for the copies).
/// </para>
/// <para>
/// The copies: an attributed partial class binds the whole library, chosen Files, or a Folder,
/// as copies; the baseline keeps everything. Two of the app's classes bind a function once each
/// unless the second says AliasBinding (ASM0142). A static class gets static members, a
/// non-static one instance members (Virtual when asked). The non-Default options of the assembly
/// attribute are the copies' defaults; a class that selects Files or a Folder is Flat by default.
/// </para>
/// <para>
/// When it runs (the switch): the SDK and every asm package write the baseline themselves, as
/// source, and add this generator only when the app's code mentions AsmLibrary; then it writes
/// the baselines too, and the written ones step aside. So [AsmLibrary] is generated into the
/// compilation only when it is used. Below C# 11 (scoped locals, Utf8StringMarshaller) it writes
/// nothing and warns (ASM0147).
/// </para>
/// <para>
/// Incremental: the API files are read once per change of their text or metadata; attributed
/// classes come from ForAttributeWithMetadataName; the assembly's attributes are read from the
/// compilation (a cheap step whose equatable output stops the pipeline when nothing changed).
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
        "[AsmLibrary(\"{0}\")] names no asm library this project references with PROTO or STRUCT lines; it references {1}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0141: a Files or Folder entry is not one of the library's.</summary>
    internal static readonly DiagnosticDescriptor UnknownFile = new(
        "ASM0141", "Unknown asm file or folder",
        "[AsmLibrary(\"{0}\")]: {1} is not a file or folder of {0} that has PROTO lines; its files are {2}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0142: a function selected by a second of the app's classes without AliasBinding.</summary>
    internal static readonly DiagnosticDescriptor BoundTwice = new(
        "ASM0142", "Function bound twice",
        "{0} is already bound by {1}: the app's classes bind each function once, unless this class says AliasBinding = true (the library's namespace keeps every function anyway)",
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
    /// ASM0145: a public (or protected internal) copy of a function that takes a struct the
    /// library's baseline keeps internal. The structs live in the baseline's namespace with its
    /// visibility; the compiler would otherwise report CS0051 inside generated code, which names
    /// neither the attribute nor the fix.
    /// </summary>
    internal static readonly DiagnosticDescriptor InternalStruct = new(
        "ASM0145", "Public binding of an internal struct",
        "{0} binds {1} as {2}, but {1} takes the struct {3} of {4}, which the library's baseline keeps internal: make the baseline public ([assembly: AsmLibrary(\"{4}\", DefaultVisibility = AsmVisibility.Public)], or <AsmBindingsVisibility>Public</AsmBindingsVisibility>), or this class's bindings internal",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0146: shaping options that C# cannot follow together, or a value that is not one of an option's.</summary>
    internal static readonly DiagnosticDescriptor BadShape = new(
        "ASM0146", "Bindings options that do not fit together",
        "{0}",
        "AsmFoundation.Bindings", DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>ASM0147: the project's C# is older than the bindings need; nothing is generated.</summary>
    internal static readonly DiagnosticDescriptor OldLanguage = new(
        "ASM0147", "C# too old for the generated bindings",
        "The generated bindings of {0} need C# 11 or later (scoped locals, Utf8StringMarshaller), and this project compiles C# {1}: none were generated. Target .NET 7 or later, or set LangVersion 11.",
        "AsmFoundation.Bindings", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <summary>
    /// The attribute and its enums, added to every compilation that runs the generator (the
    /// switch adds the generator only when the app mentions AsmLibrary). Internal, so two projects
    /// never clash, and conditional, so it stays out of the compiled assembly's metadata: it is
    /// read from the source, never at run time. Written for any C# version (no nullable syntax),
    /// so that a project on C# 7.3 still compiles it (CS8370 otherwise, measured) and gets
    /// ASM0147's warning rather than errors in generated code.
    /// </summary>
    internal const string AttributeSource = """
        // <auto-generated/> by AsmFoundation.Sdk's bindings generator.
        namespace AsmFoundation.Interop
        {
            /// <summary>How bindings are laid out.</summary>
            internal enum AsmNesting
            {
                /// <summary>A class per .asm file; folders as namespaces (the baseline) or nested classes (a class's copies). The default.</summary>
                Files = 0,

                /// <summary>A class per folder; a folder's files share it.</summary>
                Folders = 1,

                /// <summary>Every function in one class: Api in the baseline, the attributed class itself for copies (the default of a class that selects Files or a Folder).</summary>
                Flat = 2,
            }

            /// <summary>The accessibility of the generated members.</summary>
            internal enum AsmVisibility
            {
                /// <summary>internal (the default).</summary>
                Internal = 0,

                /// <summary>public.</summary>
                Public = 1,

                /// <summary>protected internal: instance members only (a static class holds no protected member), in public classes.</summary>
                ProtectedInternal = 2,
            }

            /// <summary>Whether the baseline's classes are static or instantiable.</summary>
            internal enum AsmClassKind
            {
                /// <summary>Static classes of static methods, each one the [DllImport] itself where it can be (the default).</summary>
                Static = 0,

                /// <summary>Instantiable classes whose methods call the native functions (for dependency injection and test doubles).</summary>
                Instance = 1,
            }

            /// <summary>
            /// Shapes an asm library's generated bindings. On the assembly: the Default* options shape a
            /// library's baseline (its namespace, Demo.Native.Add.AddInts), the other options are the
            /// defaults of every attributed class; without a name for every library, with one for that
            /// library. On a partial class: copies of the library's functions (the whole library, Files,
            /// or a Folder) bound into that class; the baseline keeps them too. A written option overrides
            /// the level above.
            /// </summary>
            [global::System.AttributeUsage(global::System.AttributeTargets.Assembly | global::System.AttributeTargets.Class, AllowMultiple = true)]
            [global::System.Diagnostics.Conditional("ASMFOUNDATION_BINDINGS_ATTRIBUTES")]
            internal sealed class AsmLibraryAttribute : global::System.Attribute
            {
                /// <summary>Options for every library (assembly level only).</summary>
                public AsmLibraryAttribute() { }

                /// <summary>Options for, or copies of, one library.</summary>
                /// <param name="library">The library's import name (its AssemblyName).</param>
                public AsmLibraryAttribute(string library) { Library = library; }

                /// <summary>The library's import name; null for every library.</summary>
                public string Library { get; }

                /// <summary>The copies' layout (nested classes per file or folder, or Flat).</summary>
                public AsmNesting Nesting { get; set; }

                /// <summary>Whether a pointer followed by its length (len, length, size, count) also gets a span overload in the copies; true by default.</summary>
                public bool SpanOverloads { get; set; }

                /// <summary>The copies' accessibility (ProtectedInternal: a non-static class only).</summary>
                public AsmVisibility Visibility { get; set; }

                /// <summary>Whether the copies in a non-static class are virtual.</summary>
                public bool Virtual { get; set; }

                /// <summary>The .asm or .s files whose functions this class copies (paths relative to the asm project; the RID qualifier and extension may be left out).</summary>
                public string[] Files { get; set; }

                /// <summary>The folder whose files' functions this class copies, with its subfolders.</summary>
                public string Folder { get; set; }

                /// <summary>Lets this class bind functions another of the app's classes binds too.</summary>
                public bool AliasBinding { get; set; }

                /// <summary>The baseline's namespace (one library: assembly level, with its name); the library's name by default.</summary>
                public string DefaultNamespace { get; set; }

                /// <summary>The baseline's layout: Files (a class per file, folders as namespaces), Folders, or Flat (everything in Api).</summary>
                public AsmNesting DefaultNesting { get; set; }

                /// <summary>Whether the baseline has span overloads; true by default.</summary>
                public bool DefaultSpanOverloads { get; set; }

                /// <summary>The baseline's accessibility.</summary>
                public AsmVisibility DefaultVisibility { get; set; }

                /// <summary>Whether the baseline's classes are static (the default) or instantiable.</summary>
                public AsmClassKind DefaultClassKind { get; set; }

                /// <summary>Whether an instantiable baseline's methods are virtual.</summary>
                public bool DefaultVirtual { get; set; }

                /// <summary>Whether an instantiable baseline's classes are sealed.</summary>
                public bool DefaultSealed { get; set; }

                /// <summary>Whether the baseline's function classes are generated at all; true by default (its structs always are).</summary>
                public bool DefaultBindings { get; set; }
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
            .Select((pair, cancellation) =>
            {
                // The library's AsmBindings item, which the SDK's targets copy onto its API file
                // as AsmBindings<Name> metadata (made visible with CompilerVisibleItemMetadata).
                AnalyzerConfigOptions file = pair.Right.GetOptions(pair.Left);
                return ApiFile.Read(pair.Left, ShapeSettings.FromItem(name => Option(file, "build_metadata.AdditionalFiles.AsmBindings" + name)), cancellation);
            })
            .Collect();

        IncrementalValueProvider<ShapeSettings> properties = context.AnalyzerConfigOptionsProvider
            .Select((options, _) => ShapeSettings.FromProperties(name => Option(options.GlobalOptions, "build_property." + name)));

        IncrementalValueProvider<int> languageVersion = context.ParseOptionsProvider
            .Select((options, _) => options is CSharpParseOptions csharp ? (int)csharp.LanguageVersion : int.MaxValue);

        IncrementalValueProvider<EquatableArray<AttributeRequest>> assemblyAttributes = context.CompilationProvider
            .Select((compilation, _) => new EquatableArray<AttributeRequest>(compilation.Assembly.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == AttributeName)
                .Select(AttributeRequest.From)
                .ToArray()));

        IncrementalValueProvider<ImmutableArray<ClassRequest>> classes = context.SyntaxProvider
            .ForAttributeWithMetadataName(AttributeName, (node, _) => node is ClassDeclarationSyntax, (syntax, _) => ClassRequest.From(syntax))
            .Collect();

        context.RegisterSourceOutput(apiFiles.Combine(properties).Combine(languageVersion).Combine(assemblyAttributes).Combine(classes),
            (output, inputs) => Generate(output, inputs.Left.Left.Left.Left, inputs.Left.Left.Left.Right, inputs.Left.Left.Right, inputs.Left.Right, inputs.Right));
    }

    /// <summary>Decides what each library's baseline and each class's copies are, reports what is wrong, and writes the sources.</summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="files">The API files, each with its library's AsmBindings item settings.</param>
    /// <param name="properties">The project's AsmBindings* properties.</param>
    /// <param name="languageVersion">The C# language version (LanguageVersion as an int).</param>
    /// <param name="assembly">The [assembly: AsmLibrary] attributes.</param>
    /// <param name="classes">The [AsmLibrary] classes.</param>
    private static void Generate(SourceProductionContext output, ImmutableArray<ApiFile> files, ShapeSettings properties, int languageVersion,
        EquatableArray<AttributeRequest> assembly, ImmutableArray<ClassRequest> classes)
    {
        if (files.IsEmpty)
        {
            return;
        }

        if (languageVersion < (int)LanguageVersion.CSharp11)
        {
            // The bindings use scoped locals and Utf8StringMarshaller: below C# 11 they would be
            // compiler errors in generated code. One warning, nothing generated.
            string names = string.Join(", ", files.Select(f => f.Library?.Library ?? System.IO.Path.GetFileName(f.Path)));
            output.ReportDiagnostic(Diagnostic.Create(OldLanguage, Location.None, names, ((LanguageVersion)languageVersion).ToDisplayString()));
            return;
        }

        var libraries = new Dictionary<string, (ApiLibraryModel Library, ShapeSettings Item)>(StringComparer.Ordinal);
        foreach (ApiFile file in files)
        {
            if (file.Library is null)
            {
                output.ReportDiagnostic(Diagnostic.Create(UnreadableApi, Location.None, file.Path, file.Error));
            }
            else if (!libraries.ContainsKey(file.Library.Library))
            {
                // One library may come through several references (a RID-specific and a
                // portable build, or a ProjectReference and a package); its files agree (the SDK
                // checked every RID), so the first.
                libraries[file.Library.Library] = (file.Library, file.Item);
            }
        }

        AttributeRequest? global = assembly.FirstOrDefault(a => a.Library is null);
        if (global?.DefaultNamespace is not null)
        {
            output.ReportDiagnostic(Diagnostic.Create(BadShape, global.Location.ToLocation(),
                "DefaultNamespace names one library's namespace: write it on [assembly: AsmLibrary(\"library\", DefaultNamespace = ...)], not for every library"));
        }

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

                if (attribute.WritesDefaults)
                {
                    output.ReportDiagnostic(Diagnostic.Create(BadShape, attribute.Location.ToLocation(),
                        $"[AsmLibrary] on {request.FullName}: the Default* options shape a library's baseline; write them on [assembly: AsmLibrary(...)]"));
                }
            }
        }

        // Each library's baseline shape and struct types, by library, for its source and for the
        // copies (which name its structs and must not be more visible than them).
        var baselines = new Dictionary<string, BaselineShape>(StringComparer.Ordinal);
        var structTypes = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach ((ApiLibraryModel library, ShapeSettings item) in libraries.Values.OrderBy(l => l.Library.Library, StringComparer.Ordinal))
        {
            AttributeRequest? perLibrary = assembly.FirstOrDefault(a => a.Library == library.Library);
            BaselineShape? baseline = ResolveBaseline(output, library, item, properties, global, perLibrary);
            if (baseline is null)
            {
                continue;
            }

            baselines[library.Library] = baseline;
            structTypes[library.Library] = library.Structs.ToDictionary(s => s.Name, s => "global::" + baseline.Namespace + "." + CSharpEmitter.Pascal(s.Name), StringComparer.Ordinal);
            WriteBaseline(output, library, baseline, structTypes[library.Library]);
        }

        // The copies: each library's functions, as each attributed class selects them.
        foreach ((ApiLibraryModel library, _) in libraries.Values.OrderBy(l => l.Library.Library, StringComparer.Ordinal))
        {
            if (!baselines.ContainsKey(library.Library))
            {
                continue;
            }

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
                        // The baseline never counts: it keeps every function. Between the app's
                        // own classes, a function is bound once unless the later class says
                        // AliasBinding.
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
        }

        foreach (KeyValuePair<ClassRequest, List<(AttributeRequest Attribute, List<ApiFunctionModel> Functions)>> entry in classMembers)
        {
            if (entry.Value.Count > 0)
            {
                WriteClass(output, entry.Key, entry.Value, global, assembly, baselines, structTypes);
            }
        }
    }

    /// <summary>
    /// A library's baseline options (<see cref="Shapes.Resolve"/>, the rule the SDK's task
    /// shares), with the assembly attributes' Default* options as the code's level. Reports
    /// ASM0146 at the attribute (or without a location, for a setting of the project).
    /// </summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="library">The library.</param>
    /// <param name="item">Its AsmBindings item settings.</param>
    /// <param name="properties">The AsmBindings* properties.</param>
    /// <param name="global">The assembly attribute for every library, or null.</param>
    /// <param name="perLibrary">The assembly attribute for this library, or null.</param>
    /// <returns>The shape, or null after ASM0146 (the library then gets nothing).</returns>
    private static BaselineShape? ResolveBaseline(SourceProductionContext output, ApiLibraryModel library, ShapeSettings item, ShapeSettings properties,
        AttributeRequest? global, AttributeRequest? perLibrary)
    {
        var problems = new List<string>();
        BaselineShape? shape = Shapes.Resolve(library.Library, Overrides(perLibrary), Overrides(global), item, properties, problems);
        Location location = (perLibrary ?? global)?.Location.ToLocation() ?? Location.None;
        foreach (string problem in problems)
        {
            output.ReportDiagnostic(Diagnostic.Create(BadShape, location, problem));
        }

        return shape;
    }

    /// <summary>An assembly attribute's Default* options, as the code's level of the baseline's precedence.</summary>
    /// <param name="attribute">The attribute, or null.</param>
    /// <returns>Its options, or null.</returns>
    private static BaselineOverrides? Overrides(AttributeRequest? attribute) =>
        attribute is null
            ? null
            : new BaselineOverrides(attribute.DefaultNamespace, attribute.DefaultNesting, attribute.DefaultSpanOverloads, attribute.DefaultVisibility,
                attribute.DefaultClassKind, attribute.DefaultVirtual, attribute.DefaultSealed, attribute.DefaultBindings);

    /// <summary>An analyzer config option's value, or null when it is missing or empty.</summary>
    /// <param name="options">The options.</param>
    /// <param name="key">The key (build_property.Name, build_metadata.AdditionalFiles.Name).</param>
    /// <returns>The value, or null.</returns>
    private static string? Option(AnalyzerConfigOptions options, string key) =>
        options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>Writes a library's baseline as one source, unless it holds nothing.</summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="library">The library.</param>
    /// <param name="baseline">Its shape.</param>
    /// <param name="structs">Its structs' C# types.</param>
    private static void WriteBaseline(SourceProductionContext output, ApiLibraryModel library, BaselineShape baseline, IReadOnlyDictionary<string, string> structs)
    {
        var body = new StringBuilder();
        var collisions = new List<string>();
        CSharpEmitter.WriteBaseline(body, library, baseline, structs, collisions);
        foreach (string collision in collisions)
        {
            output.ReportDiagnostic(Diagnostic.Create(BadClass, Location.None,
                $"In the bindings of {library.Library} (namespace {baseline.Namespace}): {collision}. Rename one of them, or lay the baseline out differently ([assembly: AsmLibrary(\"{library.Library}\", DefaultNesting = ...)])."));
        }

        if (body.Length == 0)
        {
            return;
        }

        string text = "// <auto-generated/> by AsmFoundation.Sdk's bindings generator, from " + System.IO.Path.GetFileName(library.Path) + ".\n" + body;
        output.AddSource($"AsmBindings.{Sanitize(baseline.Namespace)}.g.cs", SourceText.From(text, Encoding.UTF8));
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

    /// <summary>Writes the copies an attributed class binds, into its partial declaration.</summary>
    /// <param name="output">The generator's output.</param>
    /// <param name="request">The class.</param>
    /// <param name="bindings">Each attribute and the functions it bound.</param>
    /// <param name="global">The assembly's options for every library, or null.</param>
    /// <param name="assembly">Every assembly attribute (for the per-library options).</param>
    /// <param name="baselines">Each library's baseline shape (its structs' visibility).</param>
    /// <param name="structTypes">Each library's structs and their C# types, by library.</param>
    private static void WriteClass(SourceProductionContext output, ClassRequest request, List<(AttributeRequest Attribute, List<ApiFunctionModel> Functions)> bindings,
        AttributeRequest? global, EquatableArray<AttributeRequest> assembly, Dictionary<string, BaselineShape> baselines,
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

        // The partial declaration repeats the class's own static-ness (parts must agree); its
        // accessibility is the app's, left to its own declaration.
        CSharpEmitter.Indent(text, indent).Append(request.IsStatic ? "static partial class " : "partial class ").Append(request.Name).Append('\n');
        CSharpEmitter.Indent(text, indent).Append("{\n");
        var collisions = new List<string>();
        foreach ((AttributeRequest attribute, List<ApiFunctionModel> functions) in bindings)
        {
            AttributeRequest? perLibrary = assembly.FirstOrDefault(a => a.Library == attribute.Library);
            bool selects = attribute.Files is not null || attribute.Folder is not null;
            Nesting nesting = (Nesting)(attribute.Nesting ?? (selects ? (int)Nesting.Flat : perLibrary?.Nesting ?? global?.Nesting ?? (int)Nesting.Files));
            bool spans = attribute.SpanOverloads ?? perLibrary?.SpanOverloads ?? global?.SpanOverloads ?? true;
            int visibility = attribute.Visibility ?? perLibrary?.Visibility ?? global?.Visibility ?? 0;
            bool isVirtual = attribute.Virtual ?? perLibrary?.Virtual ?? global?.Virtual ?? false;
            List<string> problems = Shapes.Problems($"[AsmLibrary] on {request.FullName}", !request.IsStatic, visibility, isVirtual, isSealed: false).ToList();
            foreach (string problem in problems)
            {
                output.ReportDiagnostic(Diagnostic.Create(BadShape, attribute.Location.ToLocation(), problem));
            }

            if (problems.Count > 0)
            {
                continue;
            }

            var shape = new MemberShape(MemberShape.AccessOf(visibility), request.IsStatic, isVirtual);
            if (shape.Access != "internal" && baselines[attribute.Library!].Members.TypeAccess == "internal")
            {
                // A public (or protected internal) copy taking an internal struct is CS0051 in
                // the generated code: reported here, at the attribute, with the fix. Once per
                // attribute, at its first such function.
                foreach (ApiFunctionModel function in functions)
                {
                    if (StructIn(function) is string structName)
                    {
                        output.ReportDiagnostic(Diagnostic.Create(InternalStruct, attribute.Location.ToLocation(), request.FullName, function.Name, shape.Access, structName, attribute.Library));
                        break;
                    }
                }
            }

            CSharpEmitter.WriteMembers(text, indent + 1, attribute.Library!, functions, nesting, spans, shape, request.Name, collisions, structTypes[attribute.Library!]);
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

    /// <summary>A name safe in a generated file's hint name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Letters, digits, dots and underscores only.</returns>
    private static string Sanitize(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_').ToArray());
}

/// <summary>An API file, read, or the reason it could not be, with its library's AsmBindings item settings.</summary>
/// <param name="Path">The file.</param>
/// <param name="Library">The library, or null.</param>
/// <param name="Error">Why it could not be read, or null.</param>
/// <param name="Item">The project's AsmBindings item settings for the library.</param>
internal sealed record ApiFile(string Path, ApiLibraryModel? Library, string? Error, ShapeSettings Item)
{
    /// <summary>Reads an AdditionalFiles item.</summary>
    /// <param name="text">The item.</param>
    /// <param name="item">Its AsmBindings settings (metadata).</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The file, read or not.</returns>
    internal static ApiFile Read(AdditionalText text, ShapeSettings item, System.Threading.CancellationToken cancellation)
    {
        SourceText? content = text.GetText(cancellation);
        if (content is null)
        {
            return new ApiFile(text.Path, null, "the file cannot be read", item);
        }

        try
        {
            return new ApiFile(text.Path, ApiLibraryModel.Parse(content.ToString(), text.Path), null, item);
        }
        catch (FormatException ex)
        {
            return new ApiFile(text.Path, null, ex.Message, item);
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
/// <param name="Nesting">AsmNesting as an int, when written (the copies).</param>
/// <param name="SpanOverloads">When written (the copies).</param>
/// <param name="Visibility">AsmVisibility as an int, when written (the copies).</param>
/// <param name="Virtual">When written (the copies of a non-static class).</param>
/// <param name="Files">When written.</param>
/// <param name="Folder">When written.</param>
/// <param name="AliasBinding">When written.</param>
/// <param name="DefaultNamespace">When written (the baseline).</param>
/// <param name="DefaultNesting">When written (the baseline).</param>
/// <param name="DefaultSpanOverloads">When written (the baseline).</param>
/// <param name="DefaultVisibility">When written (the baseline).</param>
/// <param name="DefaultClassKind">AsmClassKind as an int, when written (the baseline).</param>
/// <param name="DefaultVirtual">When written (the baseline).</param>
/// <param name="DefaultSealed">When written (the baseline).</param>
/// <param name="DefaultBindings">When written (the baseline's function classes).</param>
/// <param name="Location">The attribute's location.</param>
internal sealed record AttributeRequest(string? Library, int? Nesting, bool? SpanOverloads, int? Visibility, bool? Virtual,
    EquatableArray<string>? Files, string? Folder, bool? AliasBinding,
    string? DefaultNamespace, int? DefaultNesting, bool? DefaultSpanOverloads, int? DefaultVisibility, int? DefaultClassKind,
    bool? DefaultVirtual, bool? DefaultSealed, bool? DefaultBindings, LocationInfo Location)
{
    /// <summary>Whether the attribute writes any Default* option (which only the assembly level may).</summary>
    internal bool WritesDefaults => DefaultNamespace is not null || DefaultNesting is not null || DefaultSpanOverloads is not null
        || DefaultVisibility is not null || DefaultClassKind is not null || DefaultVirtual is not null || DefaultSealed is not null || DefaultBindings is not null;

    /// <summary>Reads an attribute: its constructor argument and only the named arguments written.</summary>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// C# attributes cannot hold "unset", so whether an option inherits from the level above is
    /// decided by whether it was written: the named arguments are exactly those.
    /// </remarks>
    internal static AttributeRequest From(AttributeData attribute)
    {
        string? library = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        int? nesting = null, visibility = null, defaultNesting = null, defaultVisibility = null, defaultClassKind = null;
        bool? spans = null, isVirtual = null, alias = null, defaultSpans = null, defaultVirtual = null, defaultSealed = null, defaultBindings = null;
        EquatableArray<string>? files = null;
        string? folder = null, defaultNamespace = null;
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            // An array's Value throws ("TypedConstant is an array. Use Values property.", measured:
            // Files = new[] { ... } failed the whole generator with CS8785), so the scalar is read
            // only from a scalar; Files reads Values below.
            object? value = argument.Value.Kind == TypedConstantKind.Array ? null : argument.Value.Value;
            switch (argument.Key)
            {
                case "Nesting": nesting = value is int n ? n : null; break;
                case "SpanOverloads": spans = value is bool s ? s : null; break;
                case "Visibility": visibility = value is int v ? v : null; break;
                case "Virtual": isVirtual = value is bool virt ? virt : null; break;
                case "Files":
                    files = argument.Value.IsNull ? null : new EquatableArray<string>(argument.Value.Values.Select(item => item.Value as string ?? "").ToArray());
                    break;
                case "Folder": folder = value as string; break;
                case "AliasBinding": alias = value is bool a ? a : null; break;
                case "DefaultNamespace": defaultNamespace = value as string; break;
                case "DefaultNesting": defaultNesting = value is int dn ? dn : null; break;
                case "DefaultSpanOverloads": defaultSpans = value is bool ds ? ds : null; break;
                case "DefaultVisibility": defaultVisibility = value is int dv ? dv : null; break;
                case "DefaultClassKind": defaultClassKind = value is int dk ? dk : null; break;
                case "DefaultVirtual": defaultVirtual = value is bool dvi ? dvi : null; break;
                case "DefaultSealed": defaultSealed = value is bool dse ? dse : null; break;
                case "DefaultBindings": defaultBindings = value is bool db ? db : null; break;
            }
        }

        return new AttributeRequest(library, nesting, spans, visibility, isVirtual, files, folder, alias,
            defaultNamespace, defaultNesting, defaultSpans, defaultVisibility, defaultClassKind, defaultVirtual, defaultSealed, defaultBindings,
            LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()));
    }
}

/// <summary>A class with [AsmLibrary]: where its partial declaration goes, and what it asks for.</summary>
/// <param name="Namespace">Its namespace ("" for the global one).</param>
/// <param name="Containers">The declarations of the classes it is nested in, outermost first ("partial class Outer").</param>
/// <param name="Name">Its name.</param>
/// <param name="FullName">Its full name, for messages and file names.</param>
/// <param name="IsStatic">Whether it is a static class (its copies are then static methods; instance methods otherwise).</param>
/// <param name="Attributes">Its [AsmLibrary] attributes.</param>
/// <param name="Problem">Why bindings cannot go into it, or null.</param>
/// <param name="Location">Its location.</param>
internal sealed record ClassRequest(string Namespace, EquatableArray<string> Containers, string Name, string FullName, bool IsStatic,
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
        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            problem = $"[AsmLibrary] on {symbol.ToDisplayString()}: the class must be partial, so that the generated members can join it";
        }
        else if (symbol.IsGenericType)
        {
            problem = $"[AsmLibrary] on {symbol.ToDisplayString()}: the class must not be generic (a native function has one signature)";
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
        return new ClassRequest(ns, new EquatableArray<string>(containers.ToArray()), symbol.Name, symbol.ToDisplayString(), symbol.IsStatic,
            new EquatableArray<AttributeRequest>(context.Attributes.Select(AttributeRequest.From).ToArray()), problem,
            LocationInfo.From(syntax.Identifier.GetLocation()));
    }
}
