using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Reads the STRUCT lines of each source of a RID and of the files it includes, lays every
/// struct out as C does on the RID, and writes, per source, the file of constants the assembler
/// reads them through (<c>rect.min</c>, <c>rect_size</c>): a NASM pre-include, or a file the
/// GNU source's wrapper includes. Runs before assembling, because the source's own code uses the
/// constants.
/// </summary>
/// <remarks>
/// <para>
/// Why the SDK and not the macros (the owner's choice of 2026-11-24, from a measurement): NASM's
/// macros can lay out <c>STRUCT crc_state, uint32_t crc, uint8_t* data</c>, but llvm-mc hands a
/// GNU macro <c>uint8_t* data</c> as one token, <c>uint8_t*data</c>, and GNU macros cannot cut
/// text, so they cannot name a pointer field. Reading the lines here keeps the C line the same
/// in both dialects, and one layout (<see cref="ApiStruct"/>) serves the constants, the API file
/// and the C# struct.
/// </para>
/// <para>
/// What is read: lines that start with STRUCT (after spaces), outside comments and outside
/// macro definitions, in the source and in the files its <c>%include</c> / <c>.include</c>
/// lines name, at the include's place; an include is found where the assemblers find it, the
/// project folder (their working directory) first, then each include folder in order (measured:
/// neither NASM 3.02 nor llvm-mc 23 looks in the including file's folder). Footguns, documented:
/// <c>%if</c> / <c>.if</c> do not hide a STRUCT line from this task; a STRUCT written by another
/// macro is not read here, and the STRUCT macro then stops the build at its line, saying so; an
/// include whose name is a macro is not followed.
/// </para>
/// <para>
/// Each source gets the structs it declares or includes, as in C, not every struct of the
/// project. Errors, at the STRUCT line: ASM0130 (the line does not parse, or a field is refused),
/// ASM0136 (a second STRUCT of the same name that declares it differently). An error in an
/// include file is reported once, however many sources include it. A source without STRUCT
/// lines gets no file, and a stale one of an earlier build is deleted.
/// </para>
/// </remarks>
public sealed class AsmReadStructs : Task
{
    /// <summary>A STRUCT line: the macro's name, then its text (or nothing, which the parser refuses with its reason).</summary>
    private static readonly Regex StructLinePattern = new(@"^STRUCT(?:\s+(?<text>.*))?$", RegexOptions.CultureInvariant);

    /// <summary>A NASM include of a literal file name: <c>%include "x.inc"</c>, <c>'x.inc'</c> or <c>&lt;x.inc&gt;</c>.</summary>
    private static readonly Regex NasmInclude = new(@"^%include\s+[""'<](?<file>[^""'>]+)[""'>]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A GNU include: <c>.include "x.inc"</c>.</summary>
    private static readonly Regex GnuInclude = new(@"^\.include\s+""(?<file>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The start of a NASM macro definition (any of its four kinds).</summary>
    private static readonly Regex NasmMacroStart = new(@"^%(?:i?r?macro)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The end of a NASM macro definition.</summary>
    private static readonly Regex NasmMacroEnd = new(@"^%(?:endmacro|endm)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The start of a GNU macro definition.</summary>
    private static readonly Regex GnuMacroStart = new(@"^\.macro\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The end of a GNU macro definition.</summary>
    private static readonly Regex GnuMacroEnd = new(@"^\.endm(?:acro)?\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>How deep includes may nest before the walk stops: cycles are caught by the visited set, this only bounds a pathological chain.</summary>
    private const int MaxIncludeDepth = 64;

    /// <summary>
    /// The RID's selected sources (_AsmSelectedSource). Metadata read: <c>AsmLanguage</c> (Nasm
    /// or Gnu), <c>AsmRecursiveDir</c> (the subfolder the constants file goes into, as the
    /// object and the wrapper do), FullPath.
    /// </summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The assemblers' include folders, in their order (_AsmIncludeDirectoryOrdered: <c>ToolPath</c> metadata, or the item's FullPath).</summary>
    public ITaskItem[] IncludeDirectories { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project folder: the assemblers' working directory, where includes are looked for first, and the base of the relative paths given to them.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = "";

    /// <summary>Where the constants files go (obj/.../asm/structs/), relative to the project folder or absolute.</summary>
    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <summary>The RID's pointer size in bytes: 8 on every RID this task runs for.</summary>
    public int PointerSize { get; set; } = 8;

    /// <summary>
    /// The sources again, with <c>AsmStructsFile</c> (the constants file, absolute) and
    /// <c>AsmStructsToolPath</c> (the same, relative to the project folder, as the assemblers
    /// are given it: an absolute path would make objects differ between checkouts) set on those
    /// that declare or include a STRUCT, and emptied on the others.
    /// </summary>
    [Output]
    public ITaskItem[] UpdatedSources { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The constants files in use (written now or unchanged), for FileWrites and the assemble step's inputs.</summary>
    [Output]
    public ITaskItem[] StructFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Reads, lays out and writes, source by source.</summary>
    /// <returns>False after ASM0130, ASM0136 or ASM0026; the sources' constants files are then not all written.</returns>
    public override bool Execute()
    {
        string projectDirectory = Path.GetFullPath(ProjectDirectory);
        string outputDirectory = Path.GetFullPath(Path.Combine(projectDirectory, OutputDirectory));
        string[] includeDirectories = IncludeDirectories
            .Select(d => d.GetMetadata("ToolPath") is { Length: > 0 } tool ? tool : d.GetMetadata("FullPath"))
            .Where(d => d.Length > 0)
            .ToArray();
        // One report per faulty line, however many sources include the file it is in.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var updated = new List<ITaskItem>(Sources.Length);
        var files = new List<ITaskItem>();

        foreach (ITaskItem source in Sources)
        {
            bool gnu = string.Equals(source.GetMetadata("AsmLanguage"), "Gnu", StringComparison.OrdinalIgnoreCase);
            var item = new TaskItem(source);
            string relativeName = (source.GetMetadata("AsmRecursiveDir") + source.GetMetadata("Filename") + source.GetMetadata("Extension") + ".inc").Replace('\\', '/');
            string constantsFile = Path.GetFullPath(Path.Combine(outputDirectory, relativeName));

            var lines = new List<StructLine>();
            Walk(source.GetMetadata("FullPath"), gnu, projectDirectory, includeDirectories, new HashSet<string>(PathComparer), lines, 0);
            List<ApiStruct>? structs = lines.Count == 0 ? new List<ApiStruct>() : LayOut(lines, reported);
            if (structs is null)
            {
                // Errors were logged at the lines; the build stops after this task.
                updated.Add(item);
                continue;
            }

            if (structs.Count == 0)
            {
                // No STRUCT (any more): no constants, and no stale file an earlier build wrote.
                item.SetMetadata("AsmStructsFile", "");
                item.SetMetadata("AsmStructsToolPath", "");
                TryDelete(constantsFile);
                updated.Add(item);
                continue;
            }

            try
            {
                AsmReadApi.WriteIfChanged(constantsFile, gnu ? ApiStruct.ToGnu(structs) : ApiStruct.ToNasm(structs));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogError(null, "ASM0026", null, source.ItemSpec, 0, 0, 0, 0,
                    "Cannot write the STRUCT constants of {0} ({1}): {2}", source.ItemSpec, constantsFile, ex.Message);
                updated.Add(item);
                continue;
            }

            item.SetMetadata("AsmStructsFile", constantsFile);
            item.SetMetadata("AsmStructsToolPath", RelativeToolPath(projectDirectory, constantsFile));
            updated.Add(item);
            files.Add(new TaskItem(constantsFile));
        }

        UpdatedSources = updated.ToArray();
        StructFiles = files.ToArray();
        return !Log.HasLoggedErrors;
    }

    /// <summary>Path comparison as the file system makes it: case-insensitive on Windows (and, by default, macOS), exact on Linux.</summary>
    private static StringComparer PathComparer =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Parses and lays out one source's STRUCT lines in order, each seeing the structs before it.
    /// A repeated declaration that is the same (one include reached twice, or one struct written
    /// in two files alike) is the same struct; a different one is ASM0136.
    /// </summary>
    /// <param name="lines">The source's STRUCT lines, in the order the assembler meets them.</param>
    /// <param name="reported">The lines already reported (file, line and code), so an include's error is logged once.</param>
    /// <returns>The structs in declaration order, or null after an error.</returns>
    private List<ApiStruct>? LayOut(List<StructLine> lines, HashSet<string> reported)
    {
        var declared = new Dictionary<string, (ApiStruct Struct, StructLine Line)>(StringComparer.Ordinal);
        var order = new List<ApiStruct>();
        bool failed = false;
        foreach (StructLine line in lines)
        {
            ApiStruct parsed;
            try
            {
                parsed = ApiStruct.Parse(line.Text, PointerSize, name => declared.TryGetValue(name, out var d) ? d.Struct : null);
            }
            catch (FormatException ex)
            {
                failed = true;
                Report(reported, line, "ASM0130", "STRUCT {0}: {1}.", line.Text, ex.Message);
                continue;
            }

            if (declared.TryGetValue(parsed.Name, out var first))
            {
                if (first.Struct.Canonical != parsed.Canonical)
                {
                    failed = true;
                    Report(reported, line, "ASM0136",
                        "STRUCT {0} declares {0} again, differently: \"{1}\" here, \"{2}\" in {3}({4}). One struct has one layout: declare it once, in an include file every source that needs it includes.",
                        parsed.Name, parsed.Canonical, first.Struct.Canonical, first.Line.File, first.Line.Number);
                }

                continue;
            }

            declared[parsed.Name] = (parsed, line);
            order.Add(parsed);
        }

        return failed ? null : order;
    }

    /// <summary>Logs an error at a STRUCT line, unless that line already had this error.</summary>
    /// <param name="reported">The lines already reported.</param>
    /// <param name="line">The line.</param>
    /// <param name="code">The error code.</param>
    /// <param name="format">The message.</param>
    /// <param name="args">Its arguments.</param>
    private void Report(HashSet<string> reported, StructLine line, string code, string format, params object[] args)
    {
        if (reported.Add($"{line.File}|{line.Number}|{code}"))
        {
            Log.LogError(null, code, null, line.File, line.Number, 0, 0, 0, format, args);
        }
    }

    /// <summary>
    /// Collects the STRUCT lines of a file, following its includes at their places: comments
    /// and macro definitions skipped, the rest of each line looked at once.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="gnu">Whether it is GNU syntax (its includes are too).</param>
    /// <param name="projectDirectory">Where includes are looked for first.</param>
    /// <param name="includeDirectories">Where they are looked for next, in order.</param>
    /// <param name="visited">The files already walked for this source: each once, which also ends include cycles.</param>
    /// <param name="found">Receives the lines.</param>
    /// <param name="depth">How deep in includes this file is.</param>
    /// <remarks>
    /// A file that cannot be read is skipped: the assembler reports a missing or unreadable
    /// include itself, at the line that names it, better than this task could.
    /// </remarks>
    private static void Walk(string path, bool gnu, string projectDirectory, string[] includeDirectories, HashSet<string> visited, List<StructLine> found, int depth)
    {
        string full = Path.GetFullPath(path);
        if (depth > MaxIncludeDepth || !visited.Add(full))
        {
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        int macroDepth = 0;
        bool inBlockComment = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string code = (gnu ? StripGnuComments(lines[i], ref inBlockComment) : StripNasmComment(lines[i])).Trim();
            if (code.Length == 0)
            {
                continue;
            }

            // A macro's body is not assembled where it is written: a STRUCT there (or an
            // include) belongs to the macro's expansions, which this task cannot see.
            if ((gnu ? GnuMacroStart : NasmMacroStart).IsMatch(code))
            {
                macroDepth++;
                continue;
            }

            if ((gnu ? GnuMacroEnd : NasmMacroEnd).IsMatch(code))
            {
                macroDepth = Math.Max(0, macroDepth - 1);
                continue;
            }

            if (macroDepth > 0)
            {
                continue;
            }

            Match include = (gnu ? GnuInclude : NasmInclude).Match(code);
            if (include.Success)
            {
                string? resolved = Resolve(include.Groups["file"].Value, projectDirectory, includeDirectories);
                if (resolved is not null)
                {
                    Walk(resolved, gnu, projectDirectory, includeDirectories, visited, found, depth + 1);
                }

                continue;
            }

            Match line = StructLinePattern.Match(code);
            if (line.Success)
            {
                found.Add(new StructLine(full, i + 1, line.Groups["text"].Value.Trim()));
            }
        }
    }

    /// <summary>Finds an include as NASM and llvm-mc do: as given when absolute, else in the working directory (the project folder), then in each include folder.</summary>
    /// <param name="name">The name in the include line.</param>
    /// <param name="projectDirectory">The project folder.</param>
    /// <param name="includeDirectories">The include folders, in order.</param>
    /// <returns>The file, or null when none exists.</returns>
    private static string? Resolve(string name, string projectDirectory, string[] includeDirectories)
    {
        try
        {
            if (Path.IsPathRooted(name))
            {
                return File.Exists(name) ? name : null;
            }

            string candidate = Path.Combine(projectDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            foreach (string directory in includeDirectories)
            {
                candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (ArgumentException)
        {
            // A name with characters no path may hold: the assembler reports it.
        }

        return null;
    }

    /// <summary>A NASM line without its comment: everything from the first ';' outside quotes ('...', "..." and `...`).</summary>
    /// <param name="line">The line.</param>
    /// <returns>The code part.</returns>
    private static string StripNasmComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"' or '`')
            {
                quote = c;
            }
            else if (c == ';')
            {
                return line.Substring(0, i);
            }
        }

        return line;
    }

    /// <summary>
    /// A GNU line without its comments: <c>//</c> to the end of the line, <c>/* ... */</c>
    /// anywhere (possibly across lines), and a line that starts with <c>#</c> (a C preprocessor
    /// line or marker, which llvm-mc does not assemble). Quotes are respected.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="inBlockComment">Whether a <c>/*</c> comment is still open from an earlier line; updated.</param>
    /// <returns>The code part.</returns>
    private static string StripGnuComments(string line, ref bool inBlockComment)
    {
        if (!inBlockComment && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
        {
            return "";
        }

        var code = new StringBuilder(line.Length);
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inBlockComment)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (inQuote)
            {
                code.Append(c);
                if (c == '\\' && i + 1 < line.Length)
                {
                    code.Append(line[++i]);
                }
                else if (c == '"')
                {
                    inQuote = false;
                }

                continue;
            }

            if (c == '"')
            {
                inQuote = true;
                code.Append(c);
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                break;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
            }
            else
            {
                code.Append(c);
            }
        }

        return code.ToString();
    }

    /// <summary>
    /// A path as the assemblers are given it: relative to the project folder (with ".." when the
    /// intermediate folder lies outside it, as with .NET's artifacts layout), forward slashes;
    /// absolute when it is on another drive. Relative because NASM and llvm-mc may record the
    /// names they are given, and an absolute one would make two checkouts' objects differ.
    /// </summary>
    /// <param name="projectDirectory">The project folder, absolute.</param>
    /// <param name="path">The file, absolute.</param>
    /// <returns>The path.</returns>
    /// <remarks>Segment by segment rather than with Uri.MakeRelativeUri, which reads a '#' in a folder name as the start of a fragment.</remarks>
    private static string RelativeToolPath(string projectDirectory, string path)
    {
        char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        string[] from = Path.GetFullPath(projectDirectory).TrimEnd(separators).Split(separators);
        string[] to = Path.GetFullPath(path).Split(separators);
        StringComparer comparer = PathComparer;
        if (from.Length == 0 || to.Length == 0 || !comparer.Equals(from[0], to[0]))
        {
            return path.Replace('\\', '/');
        }

        int common = 0;
        while (common < from.Length && common < to.Length - 1 && comparer.Equals(from[common], to[common]))
        {
            common++;
        }

        return string.Join("/", Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)));
    }

    /// <summary>Deletes a file if it exists, quietly: a stale constants file is unused anyway.</summary>
    /// <param name="path">The file.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in place: no source is given it, so it changes nothing.
        }
    }

    /// <summary>A STRUCT line and where it is.</summary>
    /// <param name="File">The file it is in (the source or an include), absolute.</param>
    /// <param name="Number">Its 1-based line number.</param>
    /// <param name="Text">The text after STRUCT.</param>
    private sealed record StructLine(string File, int Number, string Text);
}
