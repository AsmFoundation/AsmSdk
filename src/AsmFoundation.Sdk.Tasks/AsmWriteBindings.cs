using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmFoundation.Sdk.Bindings;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes the baseline of each referenced asm library's C# bindings as source, in the C# project
/// that references it (the owner's phase-4 spec, 2026-11-25): the namespace named after the
/// library, a class per .asm file, its STRUCTs, shaped by the project's AsmBindings items and
/// AsmBindings* properties. No source generator runs for it. When the app's code uses
/// [AsmLibrary], the generator writes the bindings instead: this task then only copies each
/// library's AsmBindings item onto its API file as metadata, for the generator to read.
/// </summary>
/// <remarks>
/// <para>
/// The same emitter, model and shape rules as the generator (shared source), so a project that
/// switches to the generator gets the same baseline: the code wins over the project, and the
/// task has no code-level options (an attribute is what switches the generator on).
/// </para>
/// <para>
/// One source per library, &lt;namespace&gt;.g.cs in <see cref="OutputDirectory"/>, rewritten only
/// when its text changes (so the C# compile stays incremental), and the stale sources of
/// libraries no longer referenced deleted. A library reached through two references (a
/// RID-specific and a portable build) is written once.
/// </para>
/// <para>
/// Errors: ASM0143 (a name C# could not hold twice in the baseline), ASM0144 (an API file that
/// cannot be read), ASM0146 (a setting that is no option's, or options C# cannot follow together;
/// a property's problem is logged once, though every library reads it). An AsmBindings item that
/// names no library is ASM0140 from the shared target _AsmCheckBindingsItems, which also knows the
/// asm packages' libraries (<see cref="Libraries"/> feeds it).
/// </para>
/// </remarks>
public sealed class AsmWriteBindings : Task
{
    /// <summary>The referenced libraries' API files (GetAsmApiFile).</summary>
    [Required]
    public ITaskItem[] ApiFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project's AsmBindings items: the library's import name, with Namespace, Nesting, SpanOverloads, Visibility, ClassKind, Virtual, Sealed and DefaultBindings metadata.</summary>
    public ITaskItem[] Settings { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>AsmBindingsNesting: Files, Folders or Flat.</summary>
    public string Nesting { get; set; } = "";

    /// <summary>AsmBindingsSpanOverloads: true or false.</summary>
    public string SpanOverloads { get; set; } = "";

    /// <summary>AsmBindingsVisibility: Internal, Public or ProtectedInternal.</summary>
    public string Visibility { get; set; } = "";

    /// <summary>AsmBindingsClassKind: Static or Instance.</summary>
    public string ClassKind { get; set; } = "";

    /// <summary>AsmBindingsVirtual: true or false.</summary>
    public string Virtual { get; set; } = "";

    /// <summary>AsmBindingsSealed: true or false.</summary>
    public string Sealed { get; set; } = "";

    /// <summary>AsmDefaultBindings: false leaves the baseline's function classes out (its structs stay).</summary>
    public string DefaultBindings { get; set; } = "";

    /// <summary>Where the sources go (obj/.../AsmBindings/).</summary>
    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <summary>True (the default): write the baselines. False: the generator writes them; only annotate the API files.</summary>
    public bool WriteSources { get; set; } = true;

    /// <summary>The sources written now or unchanged: Compile and FileWrites items.</summary>
    [Output]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The API files again, each with its library's AsmBindings item as AsmBindings&lt;Name&gt; metadata (empty where the item says nothing), for the generator.</summary>
    [Output]
    public ITaskItem[] AnnotatedApiFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The libraries read (their import names), in either mode: _AsmBindingsLibrary items, which the check of the AsmBindings items reads.</summary>
    [Output]
    public ITaskItem[] Libraries { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Reads, resolves and writes.</summary>
    /// <returns>False after any error.</returns>
    public override bool Execute()
    {
        ShapeSettings properties = ShapeSettings.FromProperties(name => name switch
        {
            "AsmBindingsNesting" => Nesting,
            "AsmBindingsSpanOverloads" => SpanOverloads,
            "AsmBindingsVisibility" => Visibility,
            "AsmBindingsClassKind" => ClassKind,
            "AsmBindingsVirtual" => Virtual,
            "AsmBindingsSealed" => Sealed,
            "AsmDefaultBindings" => DefaultBindings,
            _ => null,
        });

        string outputDirectory = Path.GetFullPath(OutputDirectory);
        var libraries = new Dictionary<string, (ITaskItem File, ApiLibraryModel Library)>(StringComparer.Ordinal);
        foreach (ITaskItem file in ApiFiles)
        {
            string path = file.GetMetadata("FullPath");
            try
            {
                ApiLibraryModel library = ApiLibraryModel.Parse(File.ReadAllText(path), path);
                if (!libraries.ContainsKey(library.Library))
                {
                    libraries[library.Library] = (file, library);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                Log.LogError(null, "ASM0144", null, path, 0, 0, 0, 0, "Cannot read the API file {0}: {1}", path, ex.Message);
            }
        }

        var annotated = new List<ITaskItem>();
        var sources = new List<ITaskItem>();
        // Every library reads the same properties: a property's problem is said once.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach ((ITaskItem file, ApiLibraryModel library) in libraries.Values.OrderBy(l => l.Library.Library, StringComparer.Ordinal))
        {
            ITaskItem? setting = Settings.FirstOrDefault(s => string.Equals(s.ItemSpec, library.Library, StringComparison.OrdinalIgnoreCase));
            var annotatedFile = new TaskItem(file);
            foreach (string name in ShapeSettings.ItemMetadataNames)
            {
                annotatedFile.SetMetadata("AsmBindings" + name, setting?.GetMetadata(name) ?? "");
            }

            annotated.Add(annotatedFile);
            if (!WriteSources)
            {
                continue;
            }

            ShapeSettings item = setting is null ? ShapeSettings.None : ShapeSettings.FromItem(name => setting.GetMetadata(name));
            var problems = new List<string>();
            BaselineShape? shape = Shapes.Resolve(library.Library, null, null, item, properties, problems);
            foreach (string problem in problems.Where(reported.Add))
            {
                Log.LogError(null, "ASM0146", null, null, 0, 0, 0, 0, "{0}", problem);
            }

            if (shape is null)
            {
                continue;
            }

            var structs = library.Structs.ToDictionary(s => s.Name, s => "global::" + shape.Namespace + "." + CSharpEmitter.Pascal(s.Name), StringComparer.Ordinal);
            var body = new StringBuilder();
            var collisions = new List<string>();
            CSharpEmitter.WriteBaseline(body, library, shape, structs, collisions);
            foreach (string collision in collisions)
            {
                Log.LogError(null, "ASM0143", null, null, 0, 0, 0, 0,
                    "In the bindings of {0} (namespace {1}): {2}. Rename one of them, or lay the baseline out differently (<AsmBindings Include=\"{0}\" Nesting=\"...\" />).",
                    library.Library, shape.Namespace, collision);
            }

            if (body.Length == 0)
            {
                continue;
            }

            // The header names the API file, as the generator's does; it never says the
            // attribute's name, which the switch searches the project's sources for.
            string text = "// <auto-generated/> by AsmFoundation.Sdk, from " + Path.GetFileName(library.Path) + ": the bindings' baseline, written at build time.\n" + body;
            string source = Path.Combine(outputDirectory, shape.Namespace + ".g.cs");
            try
            {
                AsmReadApi.WriteIfChanged(source, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogError(null, "ASM0026", null, source, 0, 0, 0, 0, "Cannot write the bindings of {0} ({1}): {2}", library.Library, source, ex.Message);
                continue;
            }

            sources.Add(new TaskItem(source));
        }

        // In either mode: when the generator takes over (the app started using the attribute),
        // nothing written here is current any more, and every earlier source goes.
        DeleteStale(outputDirectory, sources);

        AnnotatedApiFiles = annotated.ToArray();
        Sources = sources.ToArray();
        Libraries = libraries.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (ITaskItem)new TaskItem(k)).ToArray();
        return !Log.HasLoggedErrors;
    }

    /// <summary>Deletes the sources of an earlier build that no library writes any more (a reference removed, a namespace renamed), quietly.</summary>
    /// <param name="directory">The output folder.</param>
    /// <param name="kept">The sources written this time.</param>
    private static void DeleteStale(string directory, List<ITaskItem> kept)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var keep = new HashSet<string>(kept.Select(k => Path.GetFullPath(k.ItemSpec)), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(directory, "*.g.cs"))
        {
            if (!keep.Contains(Path.GetFullPath(file)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not compiled (it is no Compile item), so a leftover changes nothing.
                }
            }
        }
    }
}
