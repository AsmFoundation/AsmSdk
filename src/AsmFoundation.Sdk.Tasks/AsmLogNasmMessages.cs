using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Logs NASM's messages for one source as MSBuild errors and warnings, each NASM warning with its
/// warning name as its code: NASM writes them to a file of their own (its -Z option, in
/// Visual C++'s format, -Xvc), which this task reads after the assembler ran.
/// </summary>
/// <remarks>
/// <para>
/// Why: MSBuild's own reading of a tool's output (Exec's canonical error format) takes a code
/// only from "category code:", and NASM writes "warning: text [-w+name]". Its warnings reached
/// MSBuild without a code, so the command line's per-code switches could not name them
/// (measured: dotnet build -warnaserror -nowarn:number-overflow still failed on that warning;
/// -warnnotaserror:number-overflow did not keep it a warning). With the name as the code, those
/// switches work, IDEs list the code, and the code is the same name that the NoWarn,
/// WarningsAsErrors and WarningsNotAsErrors properties take (which the SDK passes to NASM as
/// -w switches). The owner's choice for AsmSdk (2026-11-26).
/// </para>
/// <para>
/// What NASM 3.02 writes there (measured), and what each becomes:
/// <list type="bullet">
/// <item><c>file(line) : warning: text [-w+name]</c>: a warning, code <c>name</c>, the bracket
/// dropped from the text (the code says it).</item>
/// <item><c>file(line) : error: text [-w+error=name]</c>: a warning NASM made an error
/// (TreatWarningsAsErrors or WarningsAsErrors): an error, code <c>name</c>.</item>
/// <item><c>file(line) : error: text</c>, and <c>fatal:</c> or <c>panic:</c>: an error without a
/// code (NASM has no name for its errors). MSBuild's reading took "fatal" for plain output.</item>
/// <item><c>file(line) : ... from macro `M' defined here</c>: where the macro that held the
/// previous message is defined; added to that message, in parentheses, so an IDE's list shows
/// both in one entry.</item>
/// <item><c>nasm : fatal: text</c> (no file and line, such as a missing input file): an error at
/// the source being assembled.</item>
/// </list>
/// Anything else is logged as it is, at high importance, so nothing NASM said is lost.
/// llvm-mc's messages (ARM64, WebAssembly) are not read here: they have no warning names, and
/// MSBuild reads their "file:line:col: warning:" form itself.
/// </para>
/// <para>
/// The task logs and returns: whether NASM failed is the caller's question (its exit code,
/// ASM0007), so an object is still deleted and the build stops with the assembler's own errors
/// first. Returns false when it logged an error, as MSBuild expects of a task.
/// </para>
/// </remarks>
public sealed class AsmLogNasmMessages : Task
{
    /// <summary>The file NASM wrote its messages to (-Z); a missing file means NASM said nothing, or never ran.</summary>
    [Required]
    public string MessagesFile { get; set; } = "";

    /// <summary>The source as NASM was given it: where a message without a file and line belongs.</summary>
    [Required]
    public string SourceFile { get; set; } = "";

    /// <summary>A NASM message with its place: <c>file(line) : rest</c>.</summary>
    private static readonly Regex Located = new(@"^(?<file>.+?)\((?<line>\d+)\) : (?<rest>.*)$", RegexOptions.CultureInvariant);

    /// <summary>A NASM message without a place: <c>nasm : category: text</c>.</summary>
    private static readonly Regex Unlocated = new(@"^(?<origin>[^\s:()][^:()]*?) : (?<rest>(?:warning|error|fatal|panic): .*)$", RegexOptions.CultureInvariant);

    /// <summary>The category and text of a message's rest: <c>warning: text</c>.</summary>
    private static readonly Regex Categorized = new(@"^(?<category>warning|error|fatal|panic): (?<text>.*)$", RegexOptions.CultureInvariant);

    /// <summary>The warning name NASM appends: <c>[-w+name]</c> or <c>[-w+error=name]</c>.</summary>
    private static readonly Regex WarningName = new(@"\s*\[-w\+(?:error=)?(?<name>[a-z0-9][a-z0-9-]*)\]\s*$", RegexOptions.CultureInvariant);

    /// <summary>Reads the messages file and logs what it holds.</summary>
    /// <returns>False when an error was logged (NASM reported one), true otherwise.</returns>
    public override bool Execute()
    {
        if (!File.Exists(MessagesFile))
        {
            return true;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(MessagesFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not a reason to hide NASM's verdict: the exit code still decides (ASM0007), and
            // the reason the messages are missing is said.
            Log.LogWarning(null, "ASM0008", null, SourceFile, 0, 0, 0, 0,
                "{0}: NASM's messages could not be read from {1} ({2}).", SourceFile, MessagesFile, ex.Message);
            return true;
        }

        foreach (NasmMessage message in Parse(lines))
        {
            Report(message);
        }

        return !Log.HasLoggedErrors;
    }

    /// <summary>One message to log: where, what kind, its code and text.</summary>
    /// <param name="File">The file it is about; empty for none.</param>
    /// <param name="Line">Its line; 0 for none.</param>
    /// <param name="Category">warning, error, fatal, panic, or "" for plain output.</param>
    /// <param name="Code">NASM's warning name, or "".</param>
    /// <param name="Text">The text, with any macro context added.</param>
    private sealed record NasmMessage(string File, int Line, string Category, string Code, string Text);

    /// <summary>
    /// Turns NASM's lines into messages: a categorized line starts one, a context line (a place
    /// without a category) is added to the message before it, any other line is plain output.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <returns>The messages, in NASM's order.</returns>
    private List<NasmMessage> Parse(IEnumerable<string> lines)
    {
        var messages = new List<NasmMessage>();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            Match located = Located.Match(line);
            if (located.Success)
            {
                string file = located.Groups["file"].Value;
                int lineNumber = int.Parse(located.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                Match categorized = Categorized.Match(located.Groups["rest"].Value);
                if (categorized.Success)
                {
                    messages.Add(Categorize(file, lineNumber, categorized));
                }
                else if (messages.Count != 0 && messages[messages.Count - 1].Category.Length != 0)
                {
                    // A context line: where the macro that held the previous message lives.
                    NasmMessage previous = messages[messages.Count - 1];
                    string context = located.Groups["rest"].Value.TrimStart('.', ' ');
                    messages[messages.Count - 1] = previous with { Text = $"{previous.Text} ({file}({lineNumber}): {context})" };
                }
                else
                {
                    messages.Add(new NasmMessage(file, lineNumber, "", "", line));
                }

                continue;
            }

            Match unlocated = Unlocated.Match(line);
            if (unlocated.Success)
            {
                // No place of its own (a missing input file): it belongs to the source.
                messages.Add(Categorize(SourceFile, 0, Categorized.Match(unlocated.Groups["rest"].Value)));
                continue;
            }

            messages.Add(new NasmMessage("", 0, "", "", line));
        }

        return messages;
    }

    /// <summary>A categorized message, with the warning name taken from its text into its code.</summary>
    /// <param name="file">Its file.</param>
    /// <param name="line">Its line (0 for none).</param>
    /// <param name="categorized">The match of <see cref="Categorized"/>.</param>
    /// <returns>The message.</returns>
    private static NasmMessage Categorize(string file, int line, Match categorized)
    {
        string text = categorized.Groups["text"].Value;
        Match name = WarningName.Match(text);
        string code = name.Success ? name.Groups["name"].Value : "";
        if (name.Success)
        {
            text = text.Substring(0, name.Index);
        }

        return new NasmMessage(file, line, categorized.Groups["category"].Value, code, text);
    }

    /// <summary>Logs one message as MSBuild's warning, error or plain message.</summary>
    /// <param name="message">The message.</param>
    /// <remarks>
    /// The text goes in as a format argument, never as the format: NASM quotes source text, which
    /// may hold braces.
    /// </remarks>
    private void Report(NasmMessage message)
    {
        switch (message.Category)
        {
            case "warning":
                Log.LogWarning(null, message.Code, null, message.File, message.Line, 0, 0, 0, "{0}", message.Text);
                break;
            case "error":
            case "fatal":
            case "panic":
                Log.LogError(null, message.Code, null, message.File, message.Line, 0, 0, 0, "{0}", message.Text);
                break;
            default:
                Log.LogMessage(MessageImportance.High, "{0}", message.Text);
                break;
        }
    }
}
