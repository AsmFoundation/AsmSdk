using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Picks the runtime identifier of a referenced asm project that serves a .NET project built
/// for one RID: the first RID in NuGet's fallback order for the .NET project's RID that the
/// asm project builds.
/// </summary>
/// <remarks>
/// <para>
/// This is NuGet's own rule for a package's runtimes/ folder, applied to a ProjectReference:
/// the .NET SDK's runtime identifier graph (RuntimeIdentifierGraphPath, the portable graph
/// unless the project sets UseRidGraph) lists, for each RID, the RIDs it can fall back to, and
/// <see cref="RuntimeGraph.Expand"/> walks them as NuGet does, so linux-musl-arm64 tries
/// linux-musl-arm64, linux-musl, linux-arm64, ... and a project that builds linux-arm64 serves
/// it (a freestanding library needs no C library, so a glibc-free build serves musl). An exact
/// match needs no graph at all.
/// </para>
/// <para>
/// Errors: ASM0040 (no compatible RID), ASM0041 (the graph cannot be read). With
/// <see cref="Optional"/>, a missing RID is the warning ASM0049 instead, and
/// <see cref="Selected"/> stays empty: the caller then builds the .NET project without this
/// asm project.
/// </para>
/// </remarks>
public sealed class AsmSelectRuntimeIdentifier : Task
{
    /// <summary>The .NET project's RuntimeIdentifier.</summary>
    [Required]
    public string RuntimeIdentifier { get; set; } = "";

    /// <summary>The RIDs the asm project builds (its RuntimeIdentifiers, or every RID AsmSdk supports for a project that lists none).</summary>
    [Required]
    public ITaskItem[] AvailableRuntimeIdentifiers { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The .NET SDK's runtime identifier graph (RuntimeIdentifierGraphPath).</summary>
    public string RuntimeIdentifierGraphPath { get; set; } = "";

    /// <summary>The asm project, for messages.</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>
    /// The asm project's OptionalOnUnlistedRuntimes: true turns a missing RID from the error
    /// ASM0040 into the warning ASM0049, with an empty <see cref="Selected"/>. A graph that
    /// cannot be read (ASM0041) stays an error either way: that is a broken .NET SDK, not a
    /// RID the library chose not to build.
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>The RID to build the asm project for, as the asm project spells it; empty when <see cref="Optional"/> let none be found.</summary>
    [Output]
    public string Selected { get; set; } = "";

    /// <summary>Selects the RID.</summary>
    /// <returns>
    /// True with <see cref="Selected"/> set, or with it empty after the warning ASM0049 for an
    /// optional asm project; false after logging ASM0040 or ASM0041.
    /// </returns>
    public override bool Execute()
    {
        string[] available = AvailableRuntimeIdentifiers.Select(i => i.ItemSpec).ToArray();
        string? exact = available.FirstOrDefault(r => string.Equals(r, RuntimeIdentifier, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            Selected = exact;
            return true;
        }

        IReadOnlyDictionary<string, string[]> graph;
        try
        {
            graph = RuntimeGraph.Read(RuntimeIdentifierGraphPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            // ArgumentException: an empty path (a project that defines no graph).
            Log.LogError(null, "ASM0041", null, RuntimeIdentifierGraphPath, 0, 0, 0, 0,
                "Cannot read the runtime identifier graph '{0}' to find the RID of {1} that serves {2}: {3}",
                RuntimeIdentifierGraphPath, ProjectName, RuntimeIdentifier, ex.Message);
            return false;
        }

        IReadOnlyList<string> fallbacks = RuntimeGraph.Expand(RuntimeIdentifier, graph);
        string? match = RuntimeGraph.BestMatch(RuntimeIdentifier, available, graph);
        if (match is not null)
        {
            Selected = match;
            Log.LogMessage(MessageImportance.Normal, "{0}: {1} is served by its {2} build (NuGet's fallback order: {3}).",
                ProjectName, RuntimeIdentifier, match, string.Join(", ", fallbacks));
            return true;
        }

        string builds = available.Length == 0 ? "nothing" : string.Join(", ", available);
        if (Optional)
        {
            // The asm project said its code is optional where it is not built: the .NET project
            // goes on without it, and its code must cope with the library's absence. A warning,
            // not a message, so that a missing library is never silent.
            Log.LogWarning(null, "ASM0049", null, null, 0, 0, 0, 0,
                "{0} builds no runtime identifier that {1} can use (it builds {2}), and it is optional on the RIDs it does not list (OptionalOnUnlistedRuntimes): this {1} build goes on without it, so the code that uses it must handle its absence (DllNotFoundException).",
                ProjectName, RuntimeIdentifier, builds);
            Selected = "";
            return true;
        }

        Log.LogError(null, "ASM0040", null, null, 0, 0, 0, 0,
            "{0} builds no runtime identifier that {1} can use. It builds {2}; {1} can use {3}. Add one of those to its RuntimeIdentifiers, or build this project for another RID. If the code copes without it, OptionalOnUnlistedRuntimes=true in {0} turns this into a warning.",
            ProjectName, RuntimeIdentifier, builds, string.Join(", ", fallbacks));
        return false;
    }
}
