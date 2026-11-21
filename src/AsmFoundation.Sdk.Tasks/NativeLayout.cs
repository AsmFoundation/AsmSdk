using System;
using System.Collections.Generic;
using System.Linq;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// BinariesCopyPath: where a .NET project's output keeps the native libraries of its asm
/// references and asm packages. One value per library, written as a preset or as a folder
/// relative to the output (or publish) folder, in which {rid} stands for the runtime identifier
/// each file was built for.
/// </summary>
/// <remarks>
/// <para>
/// The presets: Runtimes (runtimes/{rid}/native, NuGet's own place), AppFolder (next to the
/// app), RidFolders ({rid}), and Auto (the default when nothing is set: Runtimes in a portable
/// build, AppFolder in a RID-specific one, which is what NuGet does with a package's native
/// files). Any other value is a folder: "native", "native/{rid}", "lib/asm/{rid}".
/// </para>
/// <para>
/// Why every layout loads: the .NET host loads a native library from any folder its deps.json
/// names with "localPath" (measured with .NET 10: a plain native asset in a subfolder for
/// RID-specific and self-contained apps, runtimeTargets for portable ones), and a generated
/// fallback loader covers what has no host (NativeAOT) or no deps.json entry. See
/// AsmAddNativeAssetsToDepsFile and NativeLoaderSource.
/// </para>
/// <para>
/// Footgun: a folder without {rid} holds one file name once. In a portable build, the
/// libraries of two RIDs of one OS (win-x64 and win-arm64 both give calc.dll) would overwrite
/// each other there; <see cref="AsmLayOutNativeAssets"/> stops such a build (ASM0044).
/// </para>
/// </remarks>
internal static class NativeLayout
{
    /// <summary>The normalized value of "use the SDK's default" (Auto, or nothing set).</summary>
    internal const string Auto = "Auto";

    /// <summary>The placeholder for the runtime identifier a file was built for.</summary>
    internal const string RidPlaceholder = "{rid}";

    /// <summary>The folder of the Runtimes preset (and of Auto in a portable build).</summary>
    internal const string RuntimesFolder = "runtimes/{rid}/native";

    /// <summary>Characters no file name of Windows, Linux or macOS may contain (or that a path tool reads specially).</summary>
    private static readonly char[] InvalidCharacters = { '<', '>', ':', '"', '|', '?', '*' };

    /// <summary>
    /// Reads a BinariesCopyPath value: a preset, or a relative folder.
    /// </summary>
    /// <param name="raw">The value as written (metadata or property); null or blank means "not set".</param>
    /// <param name="normalized">
    /// <see cref="Auto"/>; "" for the app folder; otherwise the folder with forward slashes, no
    /// leading "./", no trailing slash, and {rid} spelled in lower case.
    /// </param>
    /// <param name="error">Why the value is rejected, when it is; null otherwise.</param>
    /// <returns>True when the value is usable (a blank value counts, as <see cref="Auto"/>).</returns>
    /// <remarks>
    /// Rejected: an absolute path (the files would leave the output folder, which publish and
    /// pack cannot follow), a ".." segment (the same), characters no OS takes in a file name,
    /// and a placeholder other than {rid}. A preset name matches without case; a folder that
    /// should carry a preset's name ("Runtimes" as a real folder) can be written "./Runtimes".
    /// </remarks>
    internal static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = Auto;
        error = null;
        string value = (raw ?? "").Trim();
        if (value.Length == 0)
        {
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "auto":
                normalized = Auto;
                return true;
            case "runtimes":
                normalized = RuntimesFolder;
                return true;
            case "appfolder":
                normalized = "";
                return true;
            case "ridfolders":
                normalized = RidPlaceholder;
                return true;
        }

        string path = value.Replace('\\', '/');
        if (path.StartsWith("/", StringComparison.Ordinal) || (path.Length >= 2 && path[1] == ':'))
        {
            error = "it is an absolute path; write a folder relative to the output folder";
            return false;
        }

        if (path.IndexOfAny(InvalidCharacters) >= 0 || path.Any(char.IsControl))
        {
            error = "it contains a character that is not allowed in a file name (< > : \" | ? * or a control character)";
            return false;
        }

        var segments = new List<string>();
        foreach (string segment in path.Split('/'))
        {
            // "a//b" and "./a" mean what they do on every OS: the empty and "." segments go.
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                error = "it leaves the output folder (..); write a folder inside it";
                return false;
            }

            segments.Add(segment);
        }

        string folder = string.Join("/", segments);
        string withoutRid = ReplaceRid(folder, "");
        if (withoutRid.IndexOf('{') >= 0 || withoutRid.IndexOf('}') >= 0)
        {
            error = "it contains a placeholder other than {rid}";
            return false;
        }

        normalized = ReplaceRid(folder, RidPlaceholder);
        return true;
    }

    /// <summary>The folder a normalized value means in one build: Auto resolved, everything else as is.</summary>
    /// <param name="normalized">A value from <see cref="TryNormalize"/>.</param>
    /// <param name="ridSpecific">Whether the .NET project builds for one RID (its RuntimeIdentifier is set).</param>
    /// <returns>"" for the app folder, otherwise a folder pattern (may contain {rid}).</returns>
    internal static string Resolve(string normalized, bool ridSpecific) =>
        normalized == Auto ? (ridSpecific ? "" : RuntimesFolder) : normalized;

    /// <summary>Where one file goes, relative to the output folder.</summary>
    /// <param name="folder">A resolved folder pattern ("" for the app folder).</param>
    /// <param name="rid">The RID the file was built for.</param>
    /// <param name="fileName">The file's name.</param>
    /// <returns>The relative path, with forward slashes.</returns>
    internal static string Destination(string folder, string rid, string fileName) =>
        folder.Length == 0 ? fileName : ReplaceRid(folder, rid) + "/" + fileName;

    /// <summary>Whether a folder pattern separates RIDs ({rid} somewhere in it).</summary>
    /// <param name="folder">A resolved folder pattern.</param>
    /// <returns>True when it contains {rid}.</returns>
    internal static bool SeparatesRids(string folder) => folder.IndexOf(RidPlaceholder, StringComparison.Ordinal) >= 0;

    /// <summary>Replaces every {rid} (in any letter case) with a value.</summary>
    /// <param name="text">The pattern.</param>
    /// <param name="value">The replacement.</param>
    /// <returns>The text with each placeholder replaced.</returns>
    private static string ReplaceRid(string text, string value)
    {
        int start = 0;
        var result = new System.Text.StringBuilder();
        while (true)
        {
            int at = text.IndexOf(RidPlaceholder, start, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return result.Append(text, start, text.Length - start).ToString();
            }

            result.Append(text, start, at - start).Append(value);
            start = at + RidPlaceholder.Length;
        }
    }
}
