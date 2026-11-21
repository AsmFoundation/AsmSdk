using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// The .NET SDK's runtime identifier graph (RuntimeIdentifierGraphPath, a runtime.json file):
/// which RIDs each RID can fall back to. The same data NuGet resolves a package's runtimes/
/// folder with, so a choice made here is the choice NuGet would make.
/// </summary>
internal static class RuntimeGraph
{
    /// <summary>Reads the "#import" lists of a runtime identifier graph.</summary>
    /// <param name="path">The graph file (runtime.json format).</param>
    /// <returns>Each RID's imports, in file order; RID names compared without case.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="FormatException">The file is not a runtime graph.</exception>
    internal static Dictionary<string, string[]> Read(string path)
    {
        var graph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Json.Parse(File.ReadAllText(path)) is not JsonObject root || root["runtimes"] is not JsonObject runtimes)
        {
            throw new FormatException("no \"runtimes\" object");
        }

        foreach (KeyValuePair<string, JsonNode> runtime in runtimes.Members)
        {
            graph[runtime.Key] = runtime.Value is JsonObject entry && entry["#import"] is JsonArray imports
                ? imports.Items.OfType<JsonString>().Select(s => s.Value).ToArray()
                : Array.Empty<string>();
        }

        return graph;
    }

    /// <summary>
    /// A RID and everything it falls back to, nearest first, each once: the breadth-first
    /// expansion NuGet's RuntimeGraph.ExpandRuntime performs (linux-musl-arm64: linux-musl-arm64,
    /// linux-musl, linux-arm64, linux, unix-arm64, unix, any, base).
    /// </summary>
    /// <param name="rid">The starting RID.</param>
    /// <param name="graph">Each RID's imports (from <see cref="Read"/>).</param>
    /// <returns>The RIDs in preference order; just <paramref name="rid"/> when the graph does not know it.</returns>
    internal static IReadOnlyList<string> Expand(string rid, IReadOnlyDictionary<string, string[]> graph)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rid);
        while (queue.Count != 0)
        {
            string current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            order.Add(current);
            if (graph.TryGetValue(current, out string[]? imports))
            {
                foreach (string import in imports)
                {
                    queue.Enqueue(import);
                }
            }
        }

        return order;
    }

    /// <summary>The first RID of <paramref name="rid"/>'s fallback order that is among <paramref name="available"/>.</summary>
    /// <param name="rid">The RID that needs a build.</param>
    /// <param name="available">The RIDs there are builds for.</param>
    /// <param name="graph">Each RID's imports.</param>
    /// <returns>The match as <paramref name="available"/> spells it, or null when none fits.</returns>
    internal static string? BestMatch(string rid, IEnumerable<string> available, IReadOnlyDictionary<string, string[]> graph)
    {
        string[] candidates = available.ToArray();
        foreach (string fallback in Expand(rid, graph))
        {
            string? match = candidates.FirstOrDefault(c => string.Equals(c, fallback, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
