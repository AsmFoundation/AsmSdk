using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// The RID outer build's half of the API file: every RID must declare the same functions the
/// same way (ASM0133), and then one file, without the per-RID fields, describes the library.
/// </summary>
/// <remarks>
/// <para>
/// Why strict: the generated C# is one class for every RID, so a function one RID lacks would
/// throw EntryPointNotFoundException there, and a signature that differs would pass wrong
/// arguments; both are found here, at build time, instead. Compared per function: the canonical
/// prototype (with parameter names, so the C# is the same whichever RID it came from), the
/// logical file (the nesting) and "fast".
/// </para>
/// <para>
/// A RID whose inner build wrote no file declared nothing: if another RID did, every function
/// is missing there. The merged file is written only when its content changes, and deleted when
/// no RID has PROTO or STRUCT lines.
/// </para>
/// <para>
/// Structs are compared the same way, on everything the C# struct is made from: the canonical
/// declaration, the size, the alignment and every field's type and offset (the RID's own
/// layout, from its pointer size). The C# struct is one for every RID, so a layout that differed
/// would read wrong fields on some RID; found here instead.
/// </para>
/// </remarks>
public sealed class AsmMergeApi : Task
{
    /// <summary>The RIDs the outer build built (one item per RID).</summary>
    [Required]
    public ITaskItem[] RuntimeIdentifiers { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The inner builds' API files (GetAsmApiFile), each with RuntimeIdentifier metadata.</summary>
    public ITaskItem[] ApiFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Where the library's API file goes.</summary>
    [Required]
    public string ApiFile { get; set; } = "";

    /// <summary>The API file's path when some RID has PROTO lines; empty otherwise.</summary>
    [Output]
    public string Written { get; set; } = "";

    /// <summary>Compares and writes.</summary>
    /// <returns>False after ASM0133 or an unreadable file (ASM0135).</returns>
    public override bool Execute()
    {
        // RID -> (function name -> its entry) and (struct name -> its entry), in the order the
        // outer build lists the RIDs.
        var byRid = new List<(string Rid, Dictionary<string, JsonObject> Functions, Dictionary<string, JsonObject> Structs)>();
        JsonObject? template = null;
        foreach (ITaskItem rid in RuntimeIdentifiers)
        {
            ITaskItem? file = ApiFiles.FirstOrDefault(f => string.Equals(f.GetMetadata("RuntimeIdentifier"), rid.ItemSpec, StringComparison.OrdinalIgnoreCase));
            var functions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var structs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (file is not null && File.Exists(file.ItemSpec))
            {
                try
                {
                    var root = (JsonObject)Json.Parse(File.ReadAllText(file.ItemSpec));
                    template ??= root;
                    foreach (JsonNode node in ((JsonArray)root["functions"]!).Items)
                    {
                        var function = (JsonObject)node;
                        functions[((JsonString)function["name"]!).Value] = function;
                    }

                    // Absent in a RID without STRUCT lines (AsmReadApi writes it only when there
                    // are some): that RID declares no struct.
                    if (root["structs"] is JsonArray structArray)
                    {
                        foreach (JsonNode node in structArray.Items)
                        {
                            var entry = (JsonObject)node;
                            structs[((JsonString)entry["name"]!).Value] = entry;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidCastException or NullReferenceException)
                {
                    Log.LogError(null, "ASM0135", null, file.ItemSpec, 0, 0, 0, 0, "Cannot read the API file {0}: {1}", file.ItemSpec, ex.Message);
                    return false;
                }
            }

            byRid.Add((rid.ItemSpec, functions, structs));
        }

        if (template is null)
        {
            if (File.Exists(ApiFile))
            {
                File.Delete(ApiFile);
            }

            return true;
        }

        foreach (string name in byRid.SelectMany(r => r.Functions.Keys).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            var spellings = byRid
                .Select(r => (r.Rid, Entry: r.Functions.TryGetValue(name, out JsonObject? f) ? f : null))
                .ToList();
            if (spellings.Select(s => s.Entry is null ? "" : Key(s.Entry)).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                Log.LogError(null, "ASM0133", null, null, 0, 0, 0, 0,
                    "The RIDs declare {0} differently: {1}. The bindings are one class for every RID: declare it the same way in every RID's source.",
                    name, string.Join("; ", spellings.Select(s => $"{s.Rid}: {Describe(s.Entry)}")));
            }
        }

        foreach (string name in byRid.SelectMany(r => r.Structs.Keys).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            var layouts = byRid
                .Select(r => (r.Rid, Entry: r.Structs.TryGetValue(name, out JsonObject? s) ? s : null))
                .ToList();
            if (layouts.Select(l => l.Entry is null ? "" : StructKey(l.Entry)).Distinct(StringComparer.Ordinal).Count() > 1)
            {
                Log.LogError(null, "ASM0133", null, null, 0, 0, 0, 0,
                    "The RIDs declare struct {0} differently: {1}. The bindings give C# one struct for every RID: declare it the same way in every RID's source.",
                    name, string.Join("; ", layouts.Select(l => $"{l.Rid}: {DescribeStruct(l.Entry)}")));
            }
        }

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        // Every RID agrees: the first RID's entries, without the per-RID fields.
        var merged = new JsonObject();
        merged["format"] = template["format"]!;
        merged["library"] = template["library"]!;
        var array = new JsonArray();
        foreach (JsonObject function in byRid[0].Functions.Values.OrderBy(f => ((JsonString)f["name"]!).Value, StringComparer.Ordinal))
        {
            var entry = new JsonObject();
            foreach (KeyValuePair<string, JsonNode> member in function.Members)
            {
                if (member.Key != "source")
                {
                    entry[member.Key] = member.Value;
                }
            }

            array.Items.Add(entry);
        }

        merged["functions"] = array;
        if (byRid[0].Structs.Count > 0)
        {
            var structArray = new JsonArray();
            foreach (JsonObject entry in byRid[0].Structs.Values.OrderBy(s => ((JsonString)s["name"]!).Value, StringComparer.Ordinal))
            {
                structArray.Items.Add(WithoutSource(entry));
            }

            merged["structs"] = structArray;
        }

        AsmReadApi.WriteIfChanged(ApiFile, Json.Write(merged) + "\n");
        Written = ApiFile;
        return true;
    }

    /// <summary>What must agree between RIDs for a struct: its whole entry but the source it was first declared in.</summary>
    /// <param name="entry">A struct's entry.</param>
    /// <returns>The comparison key: the entry's JSON text without "source".</returns>
    private static string StructKey(JsonObject entry) => Json.Write(WithoutSource(entry));

    /// <summary>A copy of an entry without its per-RID "source" member.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The copy (members shared, not cloned: nothing modifies them).</returns>
    private static JsonObject WithoutSource(JsonObject entry)
    {
        var copy = new JsonObject();
        foreach (KeyValuePair<string, JsonNode> member in entry.Members)
        {
            if (member.Key != "source")
            {
                copy[member.Key] = member.Value;
            }
        }

        return copy;
    }

    /// <summary>A struct's entry for a message: its declaration, size and source, or "nothing".</summary>
    /// <param name="entry">The entry, or null when the RID does not declare the struct.</param>
    /// <returns>The description.</returns>
    private static string DescribeStruct(JsonObject? entry) =>
        entry is null
            ? "not declared"
            : $"{((JsonString)entry["declaration"]!).Value} ({((JsonLiteral)entry["size"]!).Text} bytes, in {((JsonString)entry["source"]!).Value})";

    /// <summary>What must agree between RIDs: the canonical prototype, the logical file and "fast".</summary>
    /// <param name="function">A function's entry.</param>
    /// <returns>The comparison key.</returns>
    private static string Key(JsonObject function) =>
        $"{((JsonString)function["prototype"]!).Value}|{((JsonString)function["file"]!).Value}|{((JsonLiteral)function["fast"]!).Text}";

    /// <summary>A function's entry for a message: its prototype and source, or "nothing".</summary>
    /// <param name="function">The entry, or null when the RID does not declare the function.</param>
    /// <returns>The description.</returns>
    private static string Describe(JsonObject? function) =>
        function is null
            ? "not declared"
            : $"{((JsonString)function["prototype"]!).Value} (in {((JsonString)function["source"]!).Value})";
}
