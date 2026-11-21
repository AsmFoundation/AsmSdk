using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes the DotnetToolSettings.xml of a .NET tool package made from an asm program
/// (PackAsTool): the file dotnet tool install reads to learn the command and what to start.
/// </summary>
/// <remarks>
/// <para>
/// The format is the one the .NET 10 SDK writes for a tool with platform-specific packages
/// (measured: dotnet pack of a C# tool with RuntimeIdentifiers, SDK 11.0.100), in two kinds:
/// </para>
/// <list type="bullet">
/// <item>the top-level package (PackageType DotnetTool), tools/any/any/: the command's name and
/// one RuntimeIdentifierPackage per RID, which tells dotnet tool install which package to fetch
/// for the machine it runs on (NuGet's RID fallback picks the nearest);</item>
/// <item>each RID's package (PackageType DotnetToolRidPackage), tools/any/&lt;rid&gt;/: the command
/// with EntryPoint, the program's file, and Runner="executable", which starts it directly
/// rather than through dotnet (no .NET runtime is needed to run an asm tool).</item>
/// </list>
/// <para>
/// "any" for the framework folder: an asm program needs no .NET runtime, as a self-contained
/// .NET tool, which the SDK also packs under tools/any/. Error: ASM0052 (the file cannot be
/// written).
/// </para>
/// </remarks>
public sealed class AsmWriteToolSettings : Task
{
    /// <summary>The command users type (ToolCommandName; AssemblyName by default).</summary>
    [Required]
    public string CommandName { get; set; } = "";

    /// <summary>
    /// The program's file name inside its RID folder (hello, hello.exe), for a RID package; empty
    /// for the top-level package, which names no entry point.
    /// </summary>
    public string EntryPoint { get; set; } = "";

    /// <summary>
    /// For the top-level package: one item per RID package, the RID, with metadata PackageId (the
    /// RID package's id: the tool's id with ".&lt;rid&gt;" appended). Empty for a RID package.
    /// </summary>
    public ITaskItem[] RuntimeIdentifierPackages { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The file to write (written only when its text changes).</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Writes the settings.</summary>
    /// <returns>True on success; false after logging ASM0052.</returns>
    public override bool Execute()
    {
        try
        {
            string text = Generate();
            string? existing = File.Exists(OutputPath) ? File.ReadAllText(OutputPath) : null;
            if (existing != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
                File.WriteAllText(OutputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.LogError(null, "ASM0052", null, OutputPath, 0, 0, 0, 0, "Cannot write the .NET tool settings {0}: {1}", OutputPath, ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>The settings file's text.</summary>
    /// <returns>The XML document, indented, "\n" line ends, no BOM (the SDK's own has one; dotnet tool install reads either).</returns>
    /// <exception cref="ArgumentException">A value holds a character XML cannot carry (thrown by XmlWriter).</exception>
    internal string Generate()
    {
        var command = new XElement("Command", new XAttribute("Name", CommandName));
        if (EntryPoint.Length != 0)
        {
            command.Add(new XAttribute("EntryPoint", EntryPoint), new XAttribute("Runner", "executable"));
        }

        var tool = new XElement("DotNetCliTool", new XAttribute("Version", "2"), new XElement("Commands", command));
        if (RuntimeIdentifierPackages.Length != 0)
        {
            tool.Add(new XElement("RuntimeIdentifierPackages",
                RuntimeIdentifierPackages.Select(p => new XElement("RuntimeIdentifierPackage",
                    new XAttribute("RuntimeIdentifier", p.ItemSpec),
                    new XAttribute("Id", p.GetMetadata("PackageId"))))));
        }

        // Through a stream, not a StringBuilder: an XmlWriter over a string declares
        // encoding="utf-16", which would then sit in a UTF-8 file.
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), NewLineChars = "\n" };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, settings))
        {
            new XDocument(tool).Save(writer);
        }

        return Encoding.UTF8.GetString(output.ToArray()) + "\n";
    }
}
