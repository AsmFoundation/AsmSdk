using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Removes one dependency from the nuspec inside packed .nupkg files: the asm project's
/// implicit reference to AsmFoundation.Sdk, which must flow to .NET projects through a
/// ProjectReference but must not become a dependency of the asm project's package.
/// </summary>
/// <remarks>
/// <para>
/// Why after the fact: NuGet's PackTask takes package dependencies from the project's
/// assets file, where the only switch is PrivateAssets, and PrivateAssets=all would also stop
/// the reference from flowing to a referencing .NET project (whose consumer targets come from
/// that reference). Left in the package, the dependency would tie every consumer to the SDK
/// version the package was built with: an asm project on an older SDK would fail restore with
/// NU1605 (a package downgrade, an error in .NET projects).
/// </para>
/// <para>
/// The archive entry is rewritten in place with its original timestamp, so a deterministic
/// pack stays deterministic. Empty dependency groups stay: NuGet writes them itself, to say a
/// target framework is supported with no dependencies. Error: ASM0050 (the package cannot be
/// opened or holds no nuspec).
/// </para>
/// </remarks>
public sealed class AsmRemovePackageDependency : Task
{
    /// <summary>The packed files; only .nupkg files are edited (symbol packages hold no dependencies that matter).</summary>
    [Required]
    public ITaskItem[] Packages { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The dependency's package id (compared without case, as NuGet does).</summary>
    [Required]
    public string DependencyId { get; set; } = "";

    /// <summary>Edits each package.</summary>
    /// <returns>True on success; false after logging ASM0050.</returns>
    public override bool Execute()
    {
        foreach (ITaskItem package in Packages)
        {
            string path = package.GetMetadata("FullPath");
            if (!path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                int removed = RemoveFrom(path);
                if (removed != 0)
                {
                    Log.LogMessage(MessageImportance.Low, "{0}: removed the dependency on {1} (the SDK's own reference).", Path.GetFileName(path), DependencyId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or InvalidOperationException)
            {
                Log.LogError(null, "ASM0050", null, path, 0, 0, 0, 0, "Cannot remove the {0} dependency from {1}: {2}", DependencyId, path, ex.Message);
                return false;
            }
        }

        return true;
    }

    /// <summary>Removes the dependency elements from one package's nuspec.</summary>
    /// <param name="path">The .nupkg file.</param>
    /// <returns>How many dependency elements were removed.</returns>
    /// <exception cref="IOException">The file cannot be opened for writing.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be written.</exception>
    /// <exception cref="InvalidDataException">The file is not a valid zip archive.</exception>
    /// <exception cref="XmlException">The nuspec is not well-formed XML.</exception>
    /// <exception cref="InvalidOperationException">The package has no nuspec at its root.</exception>
    private int RemoveFrom(string path)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update);
        // The nuspec is the only .nuspec file at the root of a package.
        ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.FullName.IndexOf('/') < 0 && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidOperationException("no .nuspec at the package root");
        XDocument nuspec;
        using (Stream stream = entry.Open())
        {
            nuspec = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        XElement[] matches = nuspec.Descendants()
            .Where(e => e.Name.LocalName == "dependency" && string.Equals((string?)e.Attribute("id"), DependencyId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return 0;
        }

        foreach (XElement match in matches)
        {
            XElement? group = match.Parent;

            // The element's own indentation goes with it, so the file keeps NuGet's layout.
            if (match.PreviousNode is XText indentation && string.IsNullOrWhiteSpace(indentation.Value))
            {
                indentation.Remove();
            }

            match.Remove();

            // A group left with nothing but whitespace is written as NuGet writes an empty one:
            // <group targetFramework="..." />.
            if (group is not null && !group.Elements().Any())
            {
                group.RemoveNodes();
            }
        }

        DateTimeOffset timestamp = entry.LastWriteTime;
        entry.Delete();
        ZipArchiveEntry replacement = archive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
        replacement.LastWriteTime = timestamp;
        using (Stream stream = replacement.Open())
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = nuspec.Declaration is null }))
        {
            nuspec.Save(writer);
        }

        return matches.Length;
    }
}
