using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Places the asm libraries a .NET project deploys: which of them belong in this build (all of
/// them in a portable build; in a RID-specific one, each library's build for the RID that serves
/// this one best), and where each goes, by its library's BinariesCopyPath folder.
/// </summary>
/// <remarks>
/// <para>
/// Used three ways: for the build's copy lists and the publish list (ListItems set: the list's
/// own items come back with the destination in <see cref="DestinationMetadata"/>, every other
/// metadata kept), and for deps.json and the loader (no ListItems: the known files come back).
/// The same rule decides all three, so deps.json names exactly the paths the copy writes.
/// </para>
/// <para>
/// Why the files are known by path, not by the metadata of the copy items: the .NET SDK's
/// GetCopyToOutputDirectoryItems hands a referenced project's files on without their custom
/// metadata (measured: AsmLibraryName and RuntimeIdentifier were empty in the app), so the
/// caller asks the asm projects (GetAsmNativeRuntimeAssets) and the .NET libraries in between
/// (GetAsmNativeAssets) which files are theirs, and matches the lists against those paths.
/// </para>
/// <para>
/// Errors: ASM0044 (two different files would land on one path: a folder without {rid} in a
/// portable build that has two RIDs of one OS, or two libraries with one file name);
/// ASM0040 (a RID-specific build, and no build of a library serves its RID: possible for a
/// library that reaches the project only through a .NET library); ASM0041 (the RID graph).
/// </para>
/// </remarks>
public sealed class AsmLayOutNativeAssets : Task
{
    /// <summary>
    /// Every asm file known to this project (libraries and their PDBs), each with RuntimeIdentifier,
    /// AsmProject (the asm project's full path), AsmLibraryName, AsmLibraryVersion and, for a PDB,
    /// AsmSymbols=true. A file may appear more than once; the first wins.
    /// </summary>
    [Required]
    public ITaskItem[] Assets { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The layouts from <see cref="AsmResolveNativeLayouts"/> (item = asm project path, metadata Folder).</summary>
    public ITaskItem[] Layouts { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project's RuntimeIdentifier: empty for a portable build.</summary>
    public string RuntimeIdentifier { get; set; } = "";

    /// <summary>The .NET SDK's runtime identifier graph, for a RID-specific build whose RID no library builds exactly.</summary>
    public string RuntimeIdentifierGraphPath { get; set; } = "";

    /// <summary>The metadata that receives each file's destination: TargetPath (build copy lists) or RelativePath (publish, deps.json).</summary>
    [Required]
    public string DestinationMetadata { get; set; } = "";

    /// <summary>
    /// Optional: items of a copy or publish list whose file is one of <see cref="Assets"/> (matched
    /// by the caller). They come back laid out, each file once; the files of other RIDs are left
    /// out. Without them, <see cref="Assets"/> come back.
    /// </summary>
    public ITaskItem[] ListItems { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project file, for messages.</summary>
    public string ProjectFile { get; set; } = "";

    /// <summary>The files to deploy, with their destination and the asset metadata (RuntimeIdentifier, AsmProject, AsmLibraryName, AsmLibraryVersion, AsmSymbols).</summary>
    [Output]
    public ITaskItem[] LaidOut { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The metadata copied from an asset onto a laid-out list item.</summary>
    private static readonly string[] AssetMetadata = { "RuntimeIdentifier", "AsmProject", "AsmLibraryName", "AsmLibraryVersion", "AsmSymbols", "AsmImportName" };

    /// <summary>Lays the files out.</summary>
    /// <returns>True on success; false after logging ASM0040, ASM0041 or ASM0044.</returns>
    public override bool Execute()
    {
        bool ridSpecific = RuntimeIdentifier.Length != 0;
        var assets = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem asset in Assets)
        {
            string key = FullPath(asset.ItemSpec);
            if (!assets.ContainsKey(key))
            {
                assets[key] = asset;
            }
        }

        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem layout in Layouts)
        {
            folders[FullPath(layout.ItemSpec)] = layout.GetMetadata("Folder");
        }

        // Which RID's files each library deploys here: all in a portable build; the best match in
        // a RID-specific one (the same choice AsmSelectRuntimeIdentifier made for a library the
        // project references, made here for one that only arrives through a .NET library).
        var keptRids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string[]>? graph = null;
        bool ok = true;
        foreach (IGrouping<string, ITaskItem> library in assets.Values.GroupBy(a => FullPath(a.GetMetadata("AsmProject")), StringComparer.OrdinalIgnoreCase))
        {
            if (!ridSpecific)
            {
                keptRids[library.Key] = null;
                continue;
            }

            string[] available = library.Select(a => a.GetMetadata("RuntimeIdentifier")).Where(r => r.Length != 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string? chosen = available.FirstOrDefault(r => string.Equals(r, RuntimeIdentifier, StringComparison.OrdinalIgnoreCase));
            if (chosen is null && available.Length != 0)
            {
                if (graph is null)
                {
                    try
                    {
                        graph = RuntimeGraph.Read(RuntimeIdentifierGraphPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
                    {
                        Log.LogError(null, "ASM0041", null, RuntimeIdentifierGraphPath, 0, 0, 0, 0,
                            "Cannot read the runtime identifier graph '{0}' to find the build of {1} that serves {2}: {3}",
                            RuntimeIdentifierGraphPath, Path.GetFileName(library.Key), RuntimeIdentifier, ex.Message);
                        return false;
                    }
                }

                chosen = RuntimeGraph.BestMatch(RuntimeIdentifier, available, graph);
            }

            if (chosen is null)
            {
                Log.LogError(null, "ASM0040", null, ProjectFile, 0, 0, 0, 0,
                    "{0} reaches this project with builds for {1}, and none of them serves {2}. Add a RID that serves {2} to its RuntimeIdentifiers.",
                    Path.GetFileName(library.Key), available.Length == 0 ? "no RID" : string.Join(", ", available), RuntimeIdentifier);
                ok = false;
            }

            keptRids[library.Key] = chosen;
        }

        if (!ok)
        {
            return false;
        }

        // Each kept file's destination, and who claims it.
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var claimedBy = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ITaskItem> entry in assets)
        {
            ITaskItem asset = entry.Value;
            string project = FullPath(asset.GetMetadata("AsmProject"));
            string rid = asset.GetMetadata("RuntimeIdentifier");
            if (keptRids.TryGetValue(project, out string? kept) && kept is not null && !string.Equals(kept, rid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string folder = folders.TryGetValue(project, out string? chosenFolder) ? chosenFolder : NativeLayout.Resolve(NativeLayout.Auto, ridSpecific);
            string destination = NativeLayout.Destination(folder, rid, Path.GetFileName(entry.Key));
            if (claimedBy.TryGetValue(destination, out ITaskItem? other))
            {
                LogCollision(destination, other, asset, folder);
                ok = false;
                continue;
            }

            claimedBy[destination] = asset;
            destinations[entry.Key] = destination;
        }

        if (!ok)
        {
            return false;
        }

        var laidOut = new List<ITaskItem>();
        if (ListItems.Length == 0)
        {
            foreach (KeyValuePair<string, string> placed in destinations)
            {
                var item = new TaskItem(assets[placed.Key]);
                item.SetMetadata(DestinationMetadata, placed.Value);
                laidOut.Add(item);
            }
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ITaskItem listItem in ListItems)
            {
                string key = FullPath(listItem.ItemSpec);
                if (!destinations.TryGetValue(key, out string? destination) || !seen.Add(key))
                {
                    // Another RID's file in a RID-specific build, or a second copy of one file.
                    continue;
                }

                var item = new TaskItem(listItem);
                foreach (string name in AssetMetadata)
                {
                    item.SetMetadata(name, assets[key].GetMetadata(name));
                }

                item.SetMetadata(DestinationMetadata, destination);
                laidOut.Add(item);
            }
        }

        LaidOut = laidOut.ToArray();
        return true;
    }

    /// <summary>Logs ASM0044 for two files that would land on one path, with the fix that applies.</summary>
    /// <param name="destination">The shared destination.</param>
    /// <param name="first">The file that claimed it first.</param>
    /// <param name="second">The file that collides.</param>
    /// <param name="folder">The folder of the second file's library.</param>
    private void LogCollision(string destination, ITaskItem first, ITaskItem second, string folder)
    {
        bool sameLibrary = string.Equals(FullPath(first.GetMetadata("AsmProject")), FullPath(second.GetMetadata("AsmProject")), StringComparison.OrdinalIgnoreCase);
        string fix = sameLibrary && !NativeLayout.SeparatesRids(folder)
            ? $"Add {{rid}} to its BinariesCopyPath (for example {(folder.Length == 0 ? "RidFolders" : folder + "/{rid}")}), or build fewer RIDs for it (RuntimeIdentifiers on the reference)."
            : "Give the libraries different AssemblyNames, or different BinariesCopyPath folders.";

        // Named as builds of libraries ("Calc.Native.asmproj's linux-x64 build of libcalc.so"):
        // the two full paths differ only deep inside bin/, which hides what collides.
        Log.LogError(null, "ASM0044", null, ProjectFile, 0, 0, 0, 0,
            "{0} and {1} would both be copied to {2}. {3}",
            Describe(first), Describe(second), destination, fix);
    }

    /// <summary>A file as messages name it: its library project, its RID and its name.</summary>
    /// <param name="asset">An asset item.</param>
    /// <returns>For example "Calc.Native.asmproj's linux-x64 build of libcalc.so".</returns>
    private static string Describe(ITaskItem asset) =>
        $"{Path.GetFileName(asset.GetMetadata("AsmProject"))}'s {asset.GetMetadata("RuntimeIdentifier")} build of {Path.GetFileName(asset.ItemSpec)}";

    /// <summary>A path in the form used as a key: full, with the OS's separators.</summary>
    /// <param name="path">A path as an item names it.</param>
    /// <returns>The full path; the input itself when it is empty or not a valid path.</returns>
    private static string FullPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
