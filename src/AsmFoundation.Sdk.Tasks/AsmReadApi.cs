using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Reads the API records of one RID's objects (what asmsdk.inc and asmsdk-gnu.inc write: a
/// "fn" record per function macro, a "proto" record per PROTO line, a "struct" record per
/// STRUCT line), lays the structs out, checks every PROTO line against the functions, and writes
/// the RID's API file, the input of the generated C# bindings.
/// </summary>
/// <remarks>
/// <para>
/// Why the objects and not the sources: the records are written after the assembler's
/// preprocessor, so conditional assembly, includes and macros are already resolved, and each
/// RID's per-RID source variants (osname.win.asm, osname.s) describe themselves. Comparing the
/// RIDs is <see cref="AsmMergeApi"/>'s job, in the RID outer build.
/// </para>
/// <para>
/// Errors: ASM0130 (a PROTO line that does not parse, or names a type the SDK cannot bind),
/// ASM0131 (a PROTO line for no function, or for one no image exports), ASM0132 (its
/// integer-argument count differs from FUNCTION's), ASM0134 (two PROTO lines for one function),
/// ASM0135 (an object's records cannot be read). The records carry no line numbers (NASM's
/// __?LINE?__ inside a macro is the macro's own line), so each message points at the line found
/// by searching the source for the PROTO of that function, or at the source alone.
/// </para>
/// <para>
/// The file is written only when its content changes, so a rebuild that changes no PROTO line
/// leaves it (and the C# generated from it) alone. A RID with no PROTO and no STRUCT line writes
/// no file and deletes a stale one: no API, no bindings.
/// </para>
/// <para>
/// Structs: laid out by <see cref="ApiStruct"/>, the computation <see cref="AsmReadStructs"/>
/// gave the assembler before, so the API file records the offsets the code was assembled with.
/// One struct reaches several objects when an include declares it: the same declaration is one
/// struct, a different one under the same name is ASM0136 (two sources that each declare it
/// their own way: the bindings could give C# only one). A PROTO line may point at a struct
/// (<c>const rect* r</c>), never pass one by value (ASM0130).
/// </para>
/// </remarks>
public sealed class AsmReadApi : Task
{
    /// <summary>The section name of the records in COFF and ELF objects.</summary>
    internal const string SectionName = ".asmapi";

    /// <summary>The segment of the records in Mach-O objects.</summary>
    internal const string MachOSegment = "__DWARF";

    /// <summary>The section of the records in Mach-O objects.</summary>
    internal const string MachOSection = "__asmapi";

    /// <summary>
    /// The RID's selected sources (_AsmSelectedSource): ObjectToolPath (the object),
    /// SourceToolPath (the source, absolute), SourceRelativeToolPath (relative to the project)
    /// and AsmQualifier (the file-name qualifier, empty for none).
    /// </summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The library's import name (AssemblyName): the name the bindings load.</summary>
    [Required]
    public string Library { get; set; } = "";

    /// <summary>The project's output kind (SharedLibrary, StaticLibrary, Executable, Objects), which decides which functions a PROTO may describe.</summary>
    [Required]
    public string OutputKind { get; set; } = "";

    /// <summary>The RID being built, written into the file for the comparison's messages.</summary>
    [Required]
    public string RuntimeIdentifier { get; set; } = "";

    /// <summary>Where the RID's API file goes.</summary>
    [Required]
    public string ApiFile { get; set; } = "";

    /// <summary>The RID's pointer size in bytes, for the struct layouts: 8 on every RID this task runs for.</summary>
    public int PointerSize { get; set; } = 8;

    /// <summary>The API file's path when the RID has PROTO lines; empty otherwise.</summary>
    [Output]
    public string Written { get; set; } = "";

    /// <summary>Reads, checks and writes.</summary>
    /// <returns>False after any error; the file is then neither written nor deleted.</returns>
    public override bool Execute()
    {
        var functions = new List<FunctionRecord>();
        var protos = new List<ProtoRecord>();
        var structRecords = new List<ProtoRecord>();
        foreach (ITaskItem source in Sources)
        {
            string objectFile = source.GetMetadata("ObjectToolPath");
            byte[]? section;
            try
            {
                section = ObjectSections.Read(objectFile, SectionName, MachOSegment, MachOSection);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.LogError(null, "ASM0135", null, objectFile, 0, 0, 0, 0,
                    "Cannot read the API records of {0}: {1}", objectFile, ex.Message);
                continue;
            }

            if (section is null)
            {
                // No function macro and no PROTO in this source (raw assembly, or
                // ImplicitUsings off without asmsdk.inc): nothing to bind from it.
                continue;
            }

            foreach (string record in Records(section))
            {
                if (record.StartsWith("fn ", StringComparison.Ordinal))
                {
                    FunctionRecord? function = FunctionRecord.Parse(record, source);
                    if (function is not null)
                    {
                        functions.Add(function);
                    }
                }
                else if (record.StartsWith("proto ", StringComparison.Ordinal))
                {
                    protos.Add(new ProtoRecord(record.Substring("proto ".Length), source));
                }
                else if (record.StartsWith("struct ", StringComparison.Ordinal))
                {
                    structRecords.Add(new ProtoRecord(record.Substring("struct ".Length), source));
                }

                // Any other record is for a later SDK (forward compatibility): ignored.
            }
        }

        // The structs first: PROTO lines may point at them.
        Dictionary<string, (ApiStruct Struct, ProtoRecord Record)> structs = LayOutStructs(structRecords);

        var parsed = new List<(ProtoRecord Record, ApiPrototype Prototype)>();
        foreach (ProtoRecord proto in protos)
        {
            try
            {
                parsed.Add((proto, ApiPrototype.Parse(proto.Text, structs.ContainsKey)));
            }
            catch (FormatException ex)
            {
                LogError(proto, null, "ASM0130", "PROTO {0}: {1}.", proto.Text, ex.Message);
            }
        }

        foreach (IGrouping<string, (ProtoRecord Record, ApiPrototype Prototype)> group in parsed.GroupBy(p => p.Prototype.Name, StringComparer.Ordinal))
        {
            var all = group.ToList();
            for (int i = 1; i < all.Count; i++)
            {
                // The records of one source come in source order, so this record is its
                // source's n-th PROTO of the name: the message points at that line, not at the
                // first one, which is the declaration being kept.
                int occurrence = all.Take(i).Count(p => p.Record.SourcePath == all[i].Record.SourcePath);
                Log.LogError(null, "ASM0134", null, all[i].Record.SourcePath, all[i].Record.FindLine(all[i].Prototype.Name, occurrence), 0, 0, 0,
                    "A second PROTO for {0} (the first is in {1}): declare each function once.",
                    all[i].Prototype.Name, all[0].Record.SourceRelativePath);
            }
        }

        foreach ((ProtoRecord record, ApiPrototype prototype) in parsed)
        {
            Check(record, prototype, functions);
        }

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        if (parsed.Count == 0 && structs.Count == 0)
        {
            if (File.Exists(ApiFile))
            {
                File.Delete(ApiFile);
            }

            return true;
        }

        string text = Json.Write(ToJson(parsed.GroupBy(p => p.Prototype.Name, StringComparer.Ordinal).Select(g => g.First()), structs.Values)) + "\n";
        WriteIfChanged(ApiFile, text);
        Written = ApiFile;
        return true;
    }

    /// <summary>
    /// Lays out the RID's struct records in the order the objects hold them (each object in its
    /// source's order, includes at their places), one struct per name: a repeated declaration
    /// that is the same is skipped, a different one is ASM0136.
    /// </summary>
    /// <param name="records">The struct records, in order.</param>
    /// <returns>The structs by name, each with the record that declared it first.</returns>
    /// <remarks>
    /// Every object's records are complete on their own (AsmReadStructs gave the source each struct
    /// it uses by value before assembling), so a struct is always met after the structs it holds.
    /// A record that does not parse here (ASM0130) can only come from a STRUCT the scan never saw,
    /// which the macro stops on first; the check stays, for a hand-written record.
    /// </remarks>
    private Dictionary<string, (ApiStruct Struct, ProtoRecord Record)> LayOutStructs(List<ProtoRecord> records)
    {
        var structs = new Dictionary<string, (ApiStruct Struct, ProtoRecord Record)>(StringComparer.Ordinal);
        foreach (ProtoRecord record in records)
        {
            ApiStruct laidOut;
            try
            {
                laidOut = ApiStruct.Parse(record.Text, PointerSize, name => structs.TryGetValue(name, out var s) ? s.Struct : null);
            }
            catch (FormatException ex)
            {
                Log.LogError(null, "ASM0130", null, record.SourcePath, record.FindStructLine(null), 0, 0, 0, "STRUCT {0}: {1}.", record.Text, ex.Message);
                continue;
            }

            if (structs.TryGetValue(laidOut.Name, out var first))
            {
                if (first.Struct.Canonical != laidOut.Canonical)
                {
                    Log.LogError(null, "ASM0136", null, record.SourcePath, record.FindStructLine(laidOut.Name), 0, 0, 0,
                        "STRUCT {0} declares {0} differently from {1}: \"{2}\" here, \"{3}\" there. The bindings give C# one struct per name: declare it once, in an include file both sources include.",
                        laidOut.Name, first.Record.SourceRelativePath, laidOut.Canonical, first.Struct.Canonical);
                }

                continue;
            }

            structs[laidOut.Name] = (laidOut, record);
        }

        return structs;
    }

    /// <summary>Writes a file only when its content differs, so its timestamp says when the API last changed.</summary>
    /// <param name="path">The file.</param>
    /// <param name="text">The content.</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    internal static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text)
        {
            return;
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// The logical file of a source, the unit the bindings nest by: its path relative to the
    /// project, with forward slashes, without its extension and its RID qualifier
    /// (simd/crc32c.win.asm and simd/crc32c.s are both "simd/crc32c"). A source outside the
    /// project folder keeps its file name alone.
    /// </summary>
    /// <param name="relativePath">The source relative to the project (SourceRelativeToolPath).</param>
    /// <param name="qualifier">Its qualifier (AsmQualifier), or empty.</param>
    /// <returns>The logical file.</returns>
    internal static string LogicalFile(string relativePath, string qualifier)
    {
        string path = relativePath.Replace('\\', '/');
        if (path.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(path))
        {
            path = Path.GetFileName(path);
        }

        int dot = path.LastIndexOf('.');
        if (dot > path.LastIndexOf('/'))
        {
            path = path.Substring(0, dot);
        }

        if (qualifier.Length > 0 && path.EndsWith("." + qualifier, StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(0, path.Length - qualifier.Length - 1);
        }

        return path;
    }

    /// <summary>Checks one PROTO line against the RID's functions (ASM0131, ASM0132).</summary>
    /// <param name="record">The line's record.</param>
    /// <param name="prototype">The parsed line.</param>
    /// <param name="functions">Every function record of the RID.</param>
    private void Check(ProtoRecord record, ApiPrototype prototype, List<FunctionRecord> functions)
    {
        // A shared library or a program exports FUNCTIONs only; a static library or object
        // files hand HIDDEN_FUNCTIONs to whatever links them (a NativeAOT app among them), so
        // those count there. A LOCAL_FUNCTION is never callable from outside its file.
        bool linkedIn = OutputKind is "StaticLibrary" or "Objects";
        FunctionRecord[] named = functions.Where(f => f.Name == prototype.Name).ToArray();
        FunctionRecord? callable = named.FirstOrDefault(f => f.Scope == 1 || (linkedIn && f.Scope == 2));
        if (callable is null)
        {
            string why = named.Length == 0
                ? $"no FUNCTION of this RID's sources is named {prototype.Name}"
                : $"{prototype.Name} is a {(named[0].Scope == 0 ? "LOCAL_FUNCTION, callable from its file only" : "HIDDEN_FUNCTION, which no image exports")}: declare it with FUNCTION";
            LogError(record, prototype.Name, "ASM0131", "PROTO {0} describes no function this {1} offers {2}: {3}.",
                prototype.Name, OutputKind, RuntimeIdentifier, why);
            return;
        }

        if (callable.ArgumentCount is int count && count != prototype.IntegerArgumentCount)
        {
            LogError(record, prototype.Name, "ASM0132",
                "PROTO {0} has {1} integer or pointer argument(s), but its FUNCTION in {2} says {3}. On Windows the count decides which arguments are loaded from the stack (ARG5, ARG6): make them agree.",
                prototype.Name, prototype.IntegerArgumentCount, callable.SourceRelativePath, count);
        }
    }

    /// <summary>Logs an error at the PROTO line, found by searching the source for it.</summary>
    /// <param name="record">The record.</param>
    /// <param name="name">The function's name, or null when the line did not parse that far.</param>
    /// <param name="code">The error code.</param>
    /// <param name="format">The message.</param>
    /// <param name="args">The message's arguments.</param>
    private void LogError(ProtoRecord record, string? name, string code, string format, params object[] args) =>
        Log.LogError(null, code, null, record.SourcePath, record.FindLine(name), 0, 0, 0, format, args);

    /// <summary>The API file's JSON: the library, each function and each struct, sorted by name.</summary>
    /// <param name="prototypes">One prototype per function.</param>
    /// <param name="structs">One layout per struct, with the record that declared it.</param>
    /// <returns>The document.</returns>
    /// <remarks>
    /// "structs" is written only when the RID has some, so a library without STRUCT lines keeps
    /// the file phase 2 wrote. Each struct's "source" is the source whose object declared it
    /// first (an include's struct names the first source that includes it), for messages only:
    /// the merged file leaves it out.
    /// </remarks>
    private JsonObject ToJson(IEnumerable<(ProtoRecord Record, ApiPrototype Prototype)> prototypes, IEnumerable<(ApiStruct Struct, ProtoRecord Record)> structs)
    {
        var root = new JsonObject();
        root["format"] = new JsonLiteral("1");
        root["library"] = new JsonString(Library);
        root["runtimeIdentifier"] = new JsonString(RuntimeIdentifier);
        var array = new JsonArray();
        foreach ((ProtoRecord record, ApiPrototype prototype) in prototypes.OrderBy(p => p.Prototype.Name, StringComparer.Ordinal))
        {
            var function = new JsonObject();
            function["name"] = new JsonString(prototype.Name);
            function["file"] = new JsonString(record.LogicalFile);
            function["source"] = new JsonString(record.SourceRelativePath);
            function["fast"] = new JsonLiteral(prototype.Fast ? "true" : "false");
            function["prototype"] = new JsonString(prototype.Canonical);
            function["returns"] = prototype.Returns.ToJson();
            function["parameters"] = ApiPrototype.ParametersToJson(prototype.Parameters);
            array.Items.Add(function);
        }

        root["functions"] = array;
        var structArray = new JsonArray();
        foreach ((ApiStruct laidOut, ProtoRecord record) in structs.OrderBy(s => s.Struct.Name, StringComparer.Ordinal))
        {
            JsonObject entry = laidOut.ToJson();
            entry["source"] = new JsonString(record.SourceRelativePath);
            structArray.Items.Add(entry);
        }

        if (structArray.Items.Count > 0)
        {
            root["structs"] = structArray;
        }

        return root;
    }

    /// <summary>Splits a section into its NUL-terminated records (ASCII; empty ones skipped).</summary>
    /// <param name="section">The section's bytes.</param>
    /// <returns>The records, in order.</returns>
    private static IEnumerable<string> Records(byte[] section)
    {
        int start = 0;
        for (int i = 0; i <= section.Length; i++)
        {
            if (i == section.Length || section[i] == 0)
            {
                if (i > start)
                {
                    yield return Encoding.ASCII.GetString(section, start, i - start);
                }

                start = i + 1;
            }
        }
    }

    /// <summary>A function macro's record: "fn scope name count", count "-" when not given.</summary>
    /// <param name="Scope">1 FUNCTION, 0 LOCAL_FUNCTION, 2 HIDDEN_FUNCTION.</param>
    /// <param name="Name">The function's name.</param>
    /// <param name="ArgumentCount">FUNCTION's integer-argument count, when given.</param>
    /// <param name="SourceRelativePath">The source it came from, relative to the project.</param>
    private sealed record FunctionRecord(int Scope, string Name, int? ArgumentCount, string SourceRelativePath)
    {
        /// <summary>Parses a record; null for one this SDK does not understand (a later macro's).</summary>
        /// <param name="record">The record.</param>
        /// <param name="source">The source item it came from.</param>
        /// <returns>The record, or null.</returns>
        internal static FunctionRecord? Parse(string record, ITaskItem source)
        {
            string[] parts = record.Split(' ');
            if (parts.Length != 4 || !int.TryParse(parts[1], out int scope))
            {
                return null;
            }

            int? count = int.TryParse(parts[3], out int n) ? n : null;
            return new FunctionRecord(scope, parts[2], count, source.GetMetadata("SourceRelativeToolPath"));
        }
    }

    /// <summary>A PROTO or STRUCT line's record and the source it came from.</summary>
    private sealed class ProtoRecord
    {
        /// <summary>Records the line and its source.</summary>
        /// <param name="text">The line, after "PROTO" or "STRUCT".</param>
        /// <param name="source">The source item.</param>
        internal ProtoRecord(string text, ITaskItem source)
        {
            Text = text;
            SourcePath = source.GetMetadata("SourceToolPath");
            SourceRelativePath = source.GetMetadata("SourceRelativeToolPath");
            LogicalFile = AsmReadApi.LogicalFile(SourceRelativePath, source.GetMetadata("AsmQualifier"));
        }

        /// <summary>The line, after "PROTO".</summary>
        internal string Text { get; }

        /// <summary>The source, absolute (for messages).</summary>
        internal string SourcePath { get; }

        /// <summary>The source, relative to the project.</summary>
        internal string SourceRelativePath { get; }

        /// <summary>The source's logical file (see <see cref="AsmReadApi.LogicalFile"/>).</summary>
        internal string LogicalFile { get; }

        /// <summary>
        /// The 1-based line of this function's PROTO in the source, found by searching for
        /// "PROTO ... name(" (or for the recorded text when the name is unknown); 0 when the
        /// source cannot be read or the line came from an include file or a macro.
        /// </summary>
        /// <param name="name">The function's name, or null.</param>
        /// <param name="occurrence">Which match to return, from 0: a source with two PROTO lines for one name (ASM0134) points at the second.</param>
        /// <returns>The line, or 0.</returns>
        internal int FindLine(string? name, int occurrence = 0)
        {
            try
            {
                string[] lines = File.ReadAllLines(SourcePath);
                var pattern = name is null
                    ? null
                    : new Regex(@"\bPROTO\b.*\b" + Regex.Escape(name) + @"\s*\(");
                int seen = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    if ((pattern is not null ? pattern.IsMatch(lines[i]) : lines[i].Contains("PROTO") && lines[i].Contains(Text.Trim()))
                        && seen++ == occurrence)
                    {
                        return i + 1;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a message's location is lost.
            }

            return 0;
        }

        /// <summary>
        /// The 1-based line of a struct's STRUCT line in the source, found by searching for
        /// "STRUCT name," (any STRUCT line when the name is unknown); 0 when the source cannot be
        /// read or the line is in an include file, which the message then names by its source.
        /// </summary>
        /// <param name="name">The struct's name, or null.</param>
        /// <returns>The line, or 0.</returns>
        internal int FindStructLine(string? name)
        {
            try
            {
                string[] lines = File.ReadAllLines(SourcePath);
                var pattern = new Regex(@"^\s*STRUCT\s+" + (name is null ? "" : Regex.Escape(name) + @"\s*(,|$)"));
                for (int i = 0; i < lines.Length; i++)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        return i + 1;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a message's location is lost.
            }

            return 0;
        }
    }
}
