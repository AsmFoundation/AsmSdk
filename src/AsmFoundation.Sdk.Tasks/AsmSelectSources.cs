using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Picks the sources that build one runtime identifier, from file-name qualifiers and languages.
/// </summary>
/// <remarks>
/// <para>
/// The rules, as the owner chose them ("crc32c.linux-x64.asm, or if all linuxes then
/// crc32c.linux.asm"):
/// </para>
/// <list type="bullet">
/// <item>The extension is the language. <c>.asm</c> is NASM, which assembles x86-64 only;
/// <c>.s</c> is GNU syntax for llvm-mc, which assembles ARM64 (and x86-64 in AT&amp;T syntax,
/// and WebAssembly in LLVM's WebAssembly assembly).</item>
/// <item>An optional qualifier before the extension narrows the targets: a RID
/// (<c>crc32c.linux-x64.asm</c>, <c>crc32c.browser-wasm.s</c>), an OS family (<c>win</c>,
/// <c>linux</c>, <c>linux-musl</c>, <c>osx</c>, <c>browser</c>, <c>wasi</c>), <c>unix</c>, or a
/// CPU (<c>x64</c>, <c>arm64</c>, <c>wasm</c>).</item>
/// <item>An unqualified <c>.asm</c> builds every x64 RID; an unqualified <c>.s</c> every ARM64
/// RID (GNU syntax for x86-64 must say so with a qualifier, and WebAssembly too: no unqualified
/// file ever builds browser-wasm or wasi-wasm, because no other CPU's code assembles as wasm).</item>
/// <item>Files of the same name in the same folder replace each other per RID: the most specific
/// qualifier that applies wins (see <see cref="RuntimeTarget.Specificity"/>), so a generic
/// <c>crc32c.asm</c> can be overridden for one OS without excluding anything by hand.</item>
/// </list>
/// <para>Errors: ASM0020 (two files of one name tie for a RID), ASM0021 (a NASM file qualified
/// only for ARM64 targets, or only for WebAssembly ones), ASM0022 (unsupported RID; the targets
/// check it first), ASM0024 (the same file is a Compile item twice).</para>
/// </remarks>
public sealed class AsmSelectSources : Task
{
    /// <summary>
    /// Every source of the project, of either language: its Compile items that end in .asm or
    /// .s (AsmPrepare passes no other; an .asmproj refuses another Compile item with ASM0025).
    /// </summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The runtime identifier being built.</summary>
    [Required]
    public string RuntimeIdentifier { get; set; } = "";

    /// <summary>
    /// The sources that build <see cref="RuntimeIdentifier"/>, with their metadata kept and
    /// <c>AsmLanguage</c> (Nasm or Gnu) and <c>AsmQualifier</c> added.
    /// </summary>
    [Output]
    public ITaskItem[] Selected { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Runs the selection.</summary>
    /// <returns>
    /// True when the RID is supported, no file is listed twice, and every name resolved to at
    /// most one file; false after logging ASM0020, ASM0021, ASM0022 or ASM0024.
    /// </returns>
    public override bool Execute()
    {
        if (!RuntimeTarget.All.TryGetValue(RuntimeIdentifier, out RuntimeTarget? target))
        {
            Log.LogError(null, "ASM0022", null, null, 0, 0, 0, 0,
                "AsmSdk does not build for '{0}'. Supported runtime identifiers: {1}.",
                RuntimeIdentifier, string.Join(", ", RuntimeTarget.All.Keys));
            return false;
        }

        // The same file twice (the default **/*.asm glob plus an explicit Compile item) would
        // compete with itself and be reported as a tie, and the order lookup below would throw
        // on the duplicate key. .NET reports duplicate Compile items the same way (NETSDK1022).
        string[] duplicates = Sources
            .GroupBy(s => s.ItemSpec, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicates.Length != 0)
        {
            Log.LogError(null, "ASM0024", null, duplicates[0], 0, 0, 0, 0,
                "Duplicate Compile items: {0}. The SDK includes **/*.asm and **/*.s by default; remove the explicit item, or set EnableDefaultCompileItems to false and list every source yourself.",
                string.Join(", ", duplicates));
            return false;
        }

        var candidates = new List<Candidate>();
        foreach (ITaskItem source in Sources)
        {
            Candidate candidate = Candidate.From(source);
            if (candidate.Language == "Nasm" && candidate.Qualifier.Length != 0 && QualifierIsArm64Only(candidate.Qualifier))
            {
                // NASM cannot assemble ARM64 at all; a .asm that names only ARM64 targets is a
                // mistake that would otherwise be skipped without a word on every RID.
                Log.LogError(null, "ASM0021", null, source.ItemSpec, 0, 0, 0, 0,
                    "{0} is a NASM file (.asm) qualified for '{1}', but NASM assembles x86-64 only. Write ARM64 code in GNU syntax, in a .s file.",
                    source.ItemSpec, candidate.Qualifier);
                return false;
            }

            if (candidate.Language == "Nasm" && candidate.Qualifier.Length != 0 && QualifierIsWasmOnly(candidate.Qualifier))
            {
                // The same mistake for WebAssembly (crc32c.wasm.asm): NASM has no wasm output,
                // and the file would be skipped on every RID. Same code as for ARM64: one rule,
                // "a .asm file qualified for no x86-64 RID", with the fix that fits the CPU.
                Log.LogError(null, "ASM0021", null, source.ItemSpec, 0, 0, 0, 0,
                    "{0} is a NASM file (.asm) qualified for '{1}', but NASM assembles x86-64 only. Write WebAssembly in LLVM's WebAssembly assembly, in a .s file with a wasm qualifier (name.wasm.s, or name.browser.s / name.wasi.s for one of the two).",
                    source.ItemSpec, candidate.Qualifier);
                return false;
            }

            int score = Score(candidate, target);
            if (score > 0)
            {
                candidates.Add(candidate with { Score = score });
            }
        }

        var selected = new List<ITaskItem>();
        bool ok = true;
        // Same folder and same name (qualifier and extension aside) compete; the key ignores case
        // because Windows and macOS file systems do.
        foreach (IGrouping<string, Candidate> group in candidates.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            int best = group.Max(c => c.Score);
            Candidate[] winners = group.Where(c => c.Score == best).ToArray();
            if (winners.Length > 1)
            {
                Log.LogError(null, "ASM0020", null, winners[0].Item.ItemSpec, 0, 0, 0, 0,
                    "For {0}, {1} apply equally. Keep one, or qualify one of them more narrowly (for example name.{0}.asm).",
                    target.Name, string.Join(" and ", winners.Select(w => w.Item.ItemSpec)));
                ok = false;
                continue;
            }

            var item = new TaskItem(winners[0].Item);
            item.SetMetadata("AsmLanguage", winners[0].Language);
            item.SetMetadata("AsmQualifier", winners[0].Qualifier);
            selected.Add(item);
        }

        // Keep the project's order: the link order of objects follows it, as in 0.2.0.
        var order = Sources.Select((s, i) => (s.ItemSpec, i)).ToDictionary(p => p.ItemSpec, p => p.i, StringComparer.OrdinalIgnoreCase);
        Selected = selected.OrderBy(s => order.TryGetValue(s.ItemSpec, out int i) ? i : int.MaxValue).ToArray();
        return ok;
    }

    /// <summary>How well a candidate fits the target: 0 when it does not apply.</summary>
    /// <param name="candidate">The source.</param>
    /// <param name="target">The RID being built.</param>
    /// <returns>The specificity of the qualifier, or 0 when the file's language cannot build the target or an unqualified file belongs to the other CPU.</returns>
    private static int Score(Candidate candidate, RuntimeTarget target)
    {
        if (candidate.Language == "Nasm" && target.Architecture != TargetArchitecture.X64)
        {
            return 0;
        }

        if (candidate.Qualifier.Length == 0)
        {
            // Unqualified: .asm is x64's language, .s is ARM64's. Neither is WebAssembly's: a
            // wasm RID takes qualified files only, so the ARM64 sources of a project that adds
            // browser-wasm never reach llvm-mc's wasm parser (ASM0023 names the fix instead).
            bool nativeLanguage = candidate.Language == "Nasm"
                ? target.Architecture == TargetArchitecture.X64
                : target.Architecture == TargetArchitecture.Arm64;
            return nativeLanguage ? 1 : 0;
        }

        return target.Specificity(candidate.Qualifier);
    }

    /// <summary>Whether a qualifier applies to ARM64 targets only (so a NASM file carrying it can never build).</summary>
    /// <param name="qualifier">The qualifier.</param>
    /// <returns>True when every supported RID it applies to is ARM64.</returns>
    private static bool QualifierIsArm64Only(string qualifier)
    {
        RuntimeTarget[] applies = RuntimeTarget.All.Values.Where(t => t.Specificity(qualifier) > 0).ToArray();
        return applies.Length != 0 && applies.All(t => t.Architecture == TargetArchitecture.Arm64);
    }

    /// <summary>
    /// Whether a qualifier applies to WebAssembly targets only (wasm, browser, wasi, browser-wasm,
    /// wasi-wasm), so that a NASM file carrying it can never build.
    /// </summary>
    /// <param name="qualifier">The qualifier.</param>
    /// <returns>True when every supported RID it applies to is a wasm RID.</returns>
    private static bool QualifierIsWasmOnly(string qualifier)
    {
        RuntimeTarget[] applies = RuntimeTarget.All.Values.Where(t => t.Specificity(qualifier) > 0).ToArray();
        return applies.Length != 0 && applies.All(t => t.Architecture == TargetArchitecture.Wasm);
    }

    /// <summary>A source file split into the parts the rules look at.</summary>
    /// <param name="Item">The source's Compile item.</param>
    /// <param name="Key">Folder and name without qualifier and extension: files with the same key compete.</param>
    /// <param name="Qualifier">The qualifier, or empty.</param>
    /// <param name="Language">Nasm (.asm) or Gnu (.s).</param>
    /// <param name="Score">How well it fits the target being built (set by the selection).</param>
    private sealed record Candidate(ITaskItem Item, string Key, string Qualifier, string Language, int Score)
    {
        /// <summary>Splits a source item's file name.</summary>
        /// <param name="item">The source's Compile item.</param>
        /// <returns>The candidate, scored 0 until the selection scores it.</returns>
        public static Candidate From(ITaskItem item)
        {
            string path = item.ItemSpec;
            string extension = Path.GetExtension(path);
            string language = string.Equals(extension, ".s", StringComparison.OrdinalIgnoreCase) ? "Gnu" : "Nasm";
            string stem = Path.GetFileNameWithoutExtension(path);
            string folder = Path.GetDirectoryName(path) ?? "";
            string qualifier = "";
            int dot = stem.LastIndexOf('.');
            if (dot > 0 && RuntimeTarget.IsQualifier(stem.Substring(dot + 1)))
            {
                qualifier = stem.Substring(dot + 1);
                stem = stem.Substring(0, dot);
            }

            return new Candidate(item, Path.Combine(folder, stem), qualifier, language, 0);
        }
    }
}
