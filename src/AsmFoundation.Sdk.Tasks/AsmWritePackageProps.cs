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
    /// package (native/linux-x64/freestanding/libfoo.a), with RuntimeIdentifier metadata, and
    /// AsmRuntimePackagePath when it is a shared library or its import library (the path of the
    /// file to load at run time, runtimes/&lt;rid&gt;/native/foo.dll), which a consuming program
    /// copies next to itself.
    /// </summary>
    public ITaskItem[] LinkLibraries { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The files of the program the package carries, per packed RID: the item is the path inside
    /// the package (runtimes/linux-x64/native/hello), with RuntimeIdentifier, AsmBundlePath (its
    /// path relative to the program) and AsmProgramName. A consuming asm program delivers them
    /// next to itself (AsmReferencedProgram items); empty for a library package.
    /// </summary>
    public ITaskItem[] ProgramFiles { get; set; } = Array.Empty<ITaskItem>();

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
        // The RIDs the package has something for: a library to link, or a program to deliver (a
        // program package has no link input at all, and must not read as packed for nothing).
        string[] packed = LinkLibraries.Concat(ProgramFiles).Select(l => l.GetMetadata("RuntimeIdentifier")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            // NativeLibrary, NativeAOT's item for link inputs (AsmLinkLibrary until 0.3.0: a consumer
            // on a later SDK stops with ASM0109 on a package written with that name).
            var link = new XElement("NativeLibrary",
                new XAttribute("Include", "$(MSBuildThisFileDirectory)../../" + library.ItemSpec.Replace('\\', '/')),
                new XAttribute("Condition", $"'$({ridProperty})' == '{library.GetMetadata("RuntimeIdentifier")}'"),
                new XAttribute("AsmOrigin", "Package"),
                new XAttribute("AsmPackage", PackageId));

            // A shared link input names the file a consuming program loads at run time (on
            // Windows the DLL behind the import library it links), so the program can copy it
            // next to itself. A static library has none.
            string runtime = library.GetMetadata("AsmRuntimePackagePath");
            if (runtime.Length != 0)
            {
                link.Add(new XAttribute("AsmRuntimeFile", "$(MSBuildThisFileDirectory)../../" + runtime.Replace('\\', '/')));
            }

            items.Add(link);
        }

        // A program's files, for a consuming asm program to deliver next to itself, by the same
        // RID choice as the libraries. AsmBundlePath keeps them where the program expects them.
        foreach (ITaskItem file in ProgramFiles.OrderBy(f => f.GetMetadata("RuntimeIdentifier"), StringComparer.Ordinal).ThenBy(f => f.ItemSpec, StringComparer.Ordinal))
        {
            items.Add(new XElement("AsmReferencedProgram",
                new XAttribute("Include", "$(MSBuildThisFileDirectory)../../" + file.ItemSpec.Replace('\\', '/')),
                new XAttribute("Condition", $"'$({ridProperty})' == '{file.GetMetadata("RuntimeIdentifier")}'"),
                new XAttribute("AsmOrigin", "Package"),
                new XAttribute("AsmPackage", PackageId),
                new XAttribute("AsmBundlePath", file.GetMetadata("AsmBundlePath")),
                new XAttribute("AsmProgramName", file.GetMetadata("AsmProgramName"))));
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
