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
/// <para>Errors: ASM0040 (no compatible RID), ASM0041 (the graph cannot be read).</para>
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

    /// <summary>The RID to build the asm project for, as the asm project spells it.</summary>
    [Output]
    public string Selected { get; set; } = "";

    /// <summary>Selects the RID.</summary>
    /// <returns>True with <see cref="Selected"/> set; false after logging ASM0040 or ASM0041.</returns>
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

        Log.LogError(null, "ASM0040", null, null, 0, 0, 0, 0,
            "{0} builds no runtime identifier that {1} can use. It builds {2}; {1} can use {3}. Add one of those to its RuntimeIdentifiers, or build this project for another RID.",
            ProjectName, RuntimeIdentifier, available.Length == 0 ? "nothing" : string.Join(", ", available), string.Join(", ", fallbacks));
        return false;
    }
}
