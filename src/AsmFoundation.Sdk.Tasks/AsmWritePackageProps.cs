using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes the build/&lt;nuget-tfm&gt;/&lt;id&gt;.props of an asm package for one target framework: what a
/// consuming .asmproj gets from the package, per runtime identifier.
/// </summary>
/// <remarks>
/// <para>
/// NuGet imports the props of the framework nearest to the consumer's, in every build of the
/// consumer, the RID inner builds included, where $(RuntimeIdentifier) names the RID being
/// built. The props therefore choose the package's library per RID at evaluation:
/// </para>
/// <list type="bullet">
/// <item>for each RID AsmSdk supports, the package RID that serves it, by the .NET SDK's runtime
/// identifier graph (<see cref="RuntimeGraph.BestMatch"/>): the RID itself when the package was
/// packed for it, linux-x64 for linux-musl-x64 when only that was packed (x64 code that links
/// no C library runs on either), nothing otherwise;</item>
/// <item>the include folder, for every RID;</item>
/// <item>ASM0060 from a target that runs before the consumer's AsmPrepare, when the consumer's
/// RID has no match: without it the link would fail later with undefined symbols.</item>
/// </list>
/// <para>
/// Everything is conditioned on UsingAsmFoundationSdk: a .NET consumer of the package gets its
/// runtimes/ folder from NuGet, and nothing from these props. Paths are relative to the props
/// file, so the package works from any NuGet cache location. Error: ASM0061 (the props cannot
/// be written or the graph read).
/// </para>
/// </remarks>
public sealed class AsmWritePackageProps : Task
{
    /// <summary>The package id (PackageId).</summary>
    [Required]
    public string PackageId { get; set; } = "";

    /// <summary>The package version, for the generated comment and messages.</summary>
    [Required]
    public string PackageVersion { get; set; } = "";

    /// <summary>The target framework as projects spell it (freestanding, libc).</summary>
    [Required]
    public string TargetFrameworkName { get; set; } = "";

    /// <summary>The NuGet framework behind it (netstandard1.0, netstandard2.0).</summary>
    [Required]
    public string NuGetFramework { get; set; } = "";

    /// <summary>Whether the package has an include/ folder to add to the consumer's include path.</summary>
    public bool HasIncludes { get; set; }

    /// <summary>
    /// The library a consumer links, one per packed RID: the item is the path inside the
    /// package (native/linux-x64/freestanding/libfoo.a), with RuntimeIdentifier metadata.
    /// </summary>
    public ITaskItem[] LinkLibraries { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Every RID AsmSdk builds: the consumer RIDs the props answer for.</summary>
    [Required]
    public ITaskItem[] SupportedRuntimeIdentifiers { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The .NET SDK's runtime identifier graph (RuntimeIdentifierGraphPath).</summary>
    [Required]
    public string RuntimeIdentifierGraphPath { get; set; } = "";

    /// <summary>The props file to write (written only when its text changes).</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Writes the props.</summary>
    /// <returns>True on success; false after logging ASM0061.</returns>
    public override bool Execute()
    {
        try
        {
            IReadOnlyDictionary<string, string[]> graph = RuntimeGraph.Read(RuntimeIdentifierGraphPath);
            string text = Generate(graph);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
            if (!File.Exists(OutputPath) || File.ReadAllText(OutputPath) != text)
            {
                File.WriteAllText(OutputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Log.LogError(null, "ASM0061", null, OutputPath, 0, 0, 0, 0, "Cannot write the package props {0}: {1}", OutputPath, ex.Message);
            return false;
        }
    }

    /// <summary>Builds the props text.</summary>
    /// <param name="graph">The runtime identifier graph.</param>
    /// <returns>The file's text.</returns>
    private string Generate(IReadOnlyDictionary<string, string[]> graph)
    {
        // A property-name-safe form of the id: Hello.Static -> Hello_Static.
        string id = Regex.Replace(PackageId, "[^A-Za-z0-9_]", "_");
        string ridProperty = $"_AsmPackageRid_{id}";
        string[] packed = LinkLibraries.Select(l => l.GetMetadata("RuntimeIdentifier")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        const string IsAsm = "'$(UsingAsmFoundationSdk)' == 'true'";

        var project = new XElement("Project");
        project.Add(new XComment(
            $" Generated by AsmFoundation.Sdk for {PackageId} {PackageVersion}, target framework {TargetFrameworkName} ({NuGetFramework}), " +
            $"packed for {(packed.Length == 0 ? "no runtime identifier" : string.Join(", ", packed))}. " +
            "NuGet imports the props of the framework nearest to the consuming project's; a consuming .asmproj gets the include files " +
            "and, in each RID build, the library of the package RID that serves that RID. "));

        project.Add(new XElement("PropertyGroup",
            new XElement($"AsmPackageRoot_{id}", "$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', '..'))")));

        // The package RID for each consumer RID, by the graph; consumer RIDs without a match get
        // no value, which the check target below turns into ASM0060.
        var ridGroup = new XElement("PropertyGroup", new XAttribute("Condition", $"{IsAsm} and '$(RuntimeIdentifier)' != ''"));
        foreach (string consumer in SupportedRuntimeIdentifiers.Select(i => i.ItemSpec))
        {
            string? match = RuntimeGraph.BestMatch(consumer, packed, graph);
            if (match is not null)
            {
                ridGroup.Add(new XElement(ridProperty, new XAttribute("Condition", $"'$(RuntimeIdentifier)' == '{consumer}'"), match));
            }
        }

        project.Add(ridGroup);

        var items = new XElement("ItemGroup", new XAttribute("Condition", IsAsm));
        if (HasIncludes)
        {
            items.Add(new XElement("AsmIncludeDirectory",
                new XAttribute("Include", "$(MSBuildThisFileDirectory)../../include/"),
                new XAttribute("AsmOrigin", "Package"),
                new XAttribute("AsmPackage", PackageId)));
        }

        foreach (ITaskItem library in LinkLibraries.OrderBy(l => l.GetMetadata("RuntimeIdentifier"), StringComparer.Ordinal))
        {
            items.Add(new XElement("AsmLinkLibrary",
                new XAttribute("Include", "$(MSBuildThisFileDirectory)../../" + library.ItemSpec.Replace('\\', '/')),
                new XAttribute("Condition", $"'$({ridProperty})' == '{library.GetMetadata("RuntimeIdentifier")}'"),
                new XAttribute("AsmOrigin", "Package"),
                new XAttribute("AsmPackage", PackageId)));
        }

        project.Add(items);

        project.Add(new XElement("Target",
            new XAttribute("Name", $"_AsmCheckPackage_{id}"),
            new XAttribute("BeforeTargets", "AsmPrepare"),
            new XAttribute("Condition", $"{IsAsm} and '$(RuntimeIdentifier)' != '' and '$({ridProperty})' == ''"),
            new XElement("Error",
                new XAttribute("Code", "ASM0060"),
                new XAttribute("Text",
                    $"The package {PackageId} {PackageVersion} has no build for $(RuntimeIdentifier) (target framework {TargetFrameworkName}): " +
                    $"it was packed for {(packed.Length == 0 ? "no runtime identifier" : string.Join(", ", packed))}. " +
                    "Build this project for one of those, or use a version of the package packed for $(RuntimeIdentifier)."))));

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false), NewLineChars = "\n" };
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, settings))
        {
            new XDocument(project).Save(writer);
        }

        return output.ToString() + "\n";
    }
}
