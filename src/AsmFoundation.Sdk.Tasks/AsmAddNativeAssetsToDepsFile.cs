using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Adds the native libraries of a .NET app's asm references to its deps.json, as a project
/// library whose assets name the path each file was copied to, so that the .NET host loads the
/// one for the machine the app runs on from wherever BinariesCopyPath put it.
/// </summary>
/// <remarks>
/// <para>
/// This is what NuGet gives a package's runtimes/ folder, and what the .NET SDK never does for a
/// ProjectReference: its GenerateDepsFile ignores RuntimeTargetsCopyLocalItems of project
/// libraries (measured with SDK 11.0.100). Two shapes, by the deps.json's own target (a
/// RID-specific one is named ".NETCoreApp,Version=v10.0/win-x64"), both measured with .NET 10
/// on Windows and Linux:
/// </para>
/// <list type="bullet">
/// <item>portable app: "runtimeTargets", one entry per RID, so ONE output loads the right
/// library on each machine (runtimes/&lt;rid&gt;/native/, native/&lt;rid&gt;/, &lt;rid&gt;/ alike);</item>
/// <item>RID-specific app: a plain "native" asset with "localPath". Without localPath the host
/// keeps only the asset's file name and looks in the app folder (COREHOST_TRACE: "Computed
/// relative path: calc.dll"), so a library in native/ was not found; with it, both a
/// framework-dependent and a self-contained app load it. A self-contained app ignores
/// runtimeTargets entirely, which is why they are not used there.</item>
/// </list>
/// <para>
/// Every entry carries "localPath", the path the file was copied to, as the .NET SDK writes it
/// for a package's assets. Idempotent: each library's assets are replaced, not appended to, so
/// a RID or a folder that changed disappears on the next build even when the SDK left deps.json
/// untouched. The file is written only when its text changes. Error: ASM0042 (deps.json
/// unreadable or not a deps file).
/// </para>
/// </remarks>
public sealed class AsmAddNativeAssetsToDepsFile : Task
{
    /// <summary>The deps.json to edit (ProjectDepsFilePath or PublishDepsFilePath).</summary>
    [Required]
    public string DepsFilePath { get; set; } = "";

    /// <summary>
    /// The native files (no PDBs), with metadata RelativePath (where the file is copied, relative
    /// to the app: runtimes/&lt;rid&gt;/native/&lt;file&gt;, native/&lt;file&gt;, &lt;file&gt;, ...),
    /// RuntimeIdentifier, AsmLibraryName and AsmLibraryVersion.
    /// </summary>
    [Required]
    public ITaskItem[] NativeAssets { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The app's own assembly file name (TargetFileName): its library gets a dependency on each
    /// asm library, as on any project it references.
    /// </summary>
    public string MainAssemblyFileName { get; set; } = "";

    /// <summary>Edits the file.</summary>
    /// <returns>True on success (or with nothing to add); false after logging ASM0042.</returns>
    public override bool Execute()
    {
        if (NativeAssets.Length == 0)
        {
            return true;
        }

        string original;
        JsonObject root;
        try
        {
            original = File.ReadAllText(DepsFilePath);
            root = Json.Parse(original) as JsonObject ?? throw new FormatException("the root is not an object");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.LogError(null, "ASM0042", null, DepsFilePath, 0, 0, 0, 0, "Cannot read {0} to add the asm projects' native libraries: {1}", DepsFilePath, ex.Message);
            return false;
        }

        // The target the host reads is named by runtimeTarget.name (".NETCoreApp,Version=v10.0"
        // for a portable app).
        string? targetName = (root["runtimeTarget"] as JsonObject)?["name"] is JsonString name ? name.Value : null;
        if (targetName is null || root["targets"] is not JsonObject targets)
        {
            Log.LogError(null, "ASM0042", null, DepsFilePath, 0, 0, 0, 0, "{0} has no runtimeTarget or targets: it is not a deps.json the .NET SDK wrote.", DepsFilePath);
            return false;
        }

        JsonObject target = targets.GetOrAddObject(targetName);
        JsonObject libraries = root.GetOrAddObject("libraries");
        JsonObject? main = FindMainLibrary(target);

        // A RID-specific deps.json names its RID after the framework (".NETCoreApp,Version=v10.0/win-x64").
        bool ridSpecific = targetName.IndexOf('/') >= 0;
        foreach (IGrouping<string, ITaskItem> library in NativeAssets.GroupBy(LibraryKey, StringComparer.Ordinal))
        {
            var assets = new JsonObject();
            foreach (ITaskItem asset in library.OrderBy(a => a.GetMetadata("RelativePath"), StringComparer.Ordinal))
            {
                string path = asset.GetMetadata("RelativePath").Replace('\\', '/');
                var entry = new JsonObject();
                if (!ridSpecific)
                {
                    entry["rid"] = new JsonString(asset.GetMetadata("RuntimeIdentifier"));
                    entry["assetType"] = new JsonString("native");
                }

                entry["localPath"] = new JsonString(path);
                assets[path] = entry;
            }

            // Replace both shapes: a build that switched between portable and RID-specific, or
            // that changed folders, must not keep the other shape's stale entries.
            JsonObject targetEntry = target.GetOrAddObject(library.Key);
            targetEntry["runtimeTargets"] = ridSpecific ? null : assets;
            targetEntry["native"] = ridSpecific ? assets : null;

            if (libraries[library.Key] is not JsonObject)
            {
                // A project library carries no package hash; "serviceable": false as for every
                // project the SDK lists.
                var description = new JsonObject();
                description["type"] = new JsonString("project");
                description["serviceable"] = new JsonLiteral("false");
                description["sha512"] = new JsonString("");
                libraries[library.Key] = description;
            }

            if (main is not null)
            {
                int slash = library.Key.LastIndexOf('/');
                main.GetOrAddObject("dependencies")[library.Key.Substring(0, slash)] = new JsonString(library.Key.Substring(slash + 1));
            }
        }

        string updated = Json.Write(root);
        if (updated != original)
        {
            // UTF-8 without a byte-order mark, as the SDK writes it.
            File.WriteAllText(DepsFilePath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return true;
    }

    /// <summary>The library's "name/version" key in deps.json.</summary>
    /// <param name="asset">A native asset.</param>
    /// <returns>For example "crc32c/1.0.0".</returns>
    private static string LibraryKey(ITaskItem asset) => $"{asset.GetMetadata("AsmLibraryName")}/{asset.GetMetadata("AsmLibraryVersion")}";

    /// <summary>
    /// The app's own entry in the target: the one whose runtime assets include
    /// <see cref="MainAssemblyFileName"/>, else the first entry (the SDK writes the app first).
    /// </summary>
    /// <param name="target">The runtime target.</param>
    /// <returns>The entry, or null for an empty target.</returns>
    private JsonObject? FindMainLibrary(JsonObject target)
    {
        foreach (KeyValuePair<string, JsonNode> member in target.Members)
        {
            if (member.Value is JsonObject entry && entry["runtime"] is JsonObject runtime
                && runtime.Members.Any(m => string.Equals(m.Key, MainAssemblyFileName, StringComparison.OrdinalIgnoreCase)))
            {
                return entry;
            }
        }

        return target.Members.Select(m => m.Value).OfType<JsonObject>().FirstOrDefault();
    }
}
