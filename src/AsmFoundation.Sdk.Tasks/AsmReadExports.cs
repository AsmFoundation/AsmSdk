using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Reads the names an exports file (a GNU linker version script) makes global, for the linkers
/// that take a list of names instead: lld-link (Windows, <c>-export:</c>) and ld64.lld (macOS,
/// <c>-exported_symbol</c>).
/// </summary>
/// <remarks>
/// <para>
/// One ExportsFile serves every OS: ld.lld reads it as a version script on Linux, and this task
/// turns it into names elsewhere. What only ELF has is dropped there, by necessity: Windows and
/// macOS have no symbol versions, so <c>CRC32C_1.0</c> means nothing to them, and a later
/// version node only adds names.
/// </para>
/// <para>
/// Supported: version nodes, anonymous nodes, <c>global:</c> and <c>local:</c> lists, C and
/// <c>#</c> comments. Errors (ASM0030): a wildcard in a global list on Windows, which exports
/// exact names only; an <c>extern "C++"</c> block, which would need demangling; an unreadable
/// file.
/// </para>
/// </remarks>
public sealed class AsmReadExports : Task
{
    /// <summary>The exports file (version script).</summary>
    [Required]
    public string ExportsFile { get; set; } = "";

    /// <summary>
    /// Whether wildcards may stay in the names: true for ld64.lld (macOS matches them itself),
    /// false for lld-link (Windows needs exact names).
    /// </summary>
    public bool AllowWildcards { get; set; }

    /// <summary>The global names, in file order, each once.</summary>
    [Output]
    public ITaskItem[] Names { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Reads the file.</summary>
    /// <returns>True on success; false after logging ASM0030.</returns>
    public override bool Execute()
    {
        string text;
        try
        {
            text = File.ReadAllText(ExportsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "ASM0030", null, ExportsFile, 0, 0, 0, 0, "Cannot read the exports file {0}: {1}", ExportsFile, ex.Message);
            return false;
        }

        if (text.IndexOf("extern", StringComparison.Ordinal) >= 0 && Regex.IsMatch(text, "extern\\s*\"C\\+\\+\""))
        {
            Log.LogError(null, "ASM0030", null, ExportsFile, 0, 0, 0, 0,
                "{0} uses an extern \"C++\" block; AsmSdk reads exports files as plain names on Windows and macOS. List the exported names directly.", ExportsFile);
            return false;
        }

        var names = new List<ITaskItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool inGlobal = false;
        foreach (string token in Tokens(StripComments(text)))
        {
            switch (token)
            {
                case "global:":
                    inGlobal = true;
                    continue;
                case "local:":
                case "{":
                case "}":
                    // "local:", a new node, or a node's end: names count again only after the
                    // next "global:".
                    inGlobal = false;
                    continue;
            }

            if (!inGlobal)
            {
                continue;
            }

            string name = token.TrimEnd(';');
            if (name.Length == 0)
            {
                continue;
            }

            if (!AllowWildcards && name.IndexOfAny(new[] { '*', '?', '[' }) >= 0)
            {
                Log.LogError(null, "ASM0030", null, ExportsFile, 0, 0, 0, 0,
                    "{0} exports '{1}', a pattern; Windows DLLs export exact names only. List each name.", ExportsFile, name);
                return false;
            }

            if (seen.Add(name))
            {
                names.Add(new TaskItem(name));
            }
        }

        Names = names.ToArray();
        return true;
    }

    /// <summary>Removes /* */ and # comments (version scripts allow both).</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The text without comments.</returns>
    private static string StripComments(string text)
    {
        string withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"#[^\n]*", " ");
    }

    /// <summary>
    /// Splits the script into tokens: braces, "global:"/"local:" and names (each with any ";"
    /// that ends it), so that "global: a; b;" and "global:a;b;" read the same.
    /// </summary>
    /// <param name="text">The script without comments.</param>
    /// <returns>The tokens in order.</returns>
    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == ';' || c == ':')
            {
                if (c == ':' && current.Length != 0)
                {
                    current.Append(':');
                    yield return current.ToString();
                    current.Clear();
                    continue;
                }

                if (current.Length != 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (c == '{' || c == '}')
                {
                    yield return c.ToString();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length != 0)
        {
            yield return current.ToString();
        }
    }
}
