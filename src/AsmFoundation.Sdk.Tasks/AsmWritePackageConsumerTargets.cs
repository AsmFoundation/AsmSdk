using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes buildTransitive/&lt;nuget-tfm&gt;/&lt;id&gt;.targets for a package that carries asm native
/// libraries (an asm package, or a .NET package that embeds its asm references): the
/// BinariesCopyPath support for the .NET projects that reference the package.
/// </summary>
/// <remarks>
/// <para>
/// Why in the package, and in plain MSBuild: an asm package deliberately does not depend on
/// AsmFoundation.Sdk (a consumer on an older SDK would fail restore with NU1605), so its .NET
/// consumers do not have the SDK's consumer targets or its task assembly. The targets therefore
/// do everything with MSBuild alone, and only for their own package (every name is suffixed with
/// the package id), so two packages built with different SDK versions never step on each other.
/// </para>
/// <para>
/// What they do, only when the PackageReference has BinariesCopyPath or the project sets the
/// property (otherwise NuGet's own layout stays): validate the value (ASM0043, the SDK's code);
/// after ResolvePackageAssets, move the package's native files (RuntimeTargetsCopyLocalItems of
/// a portable build, NativeCopyLocalItems of a RID-specific one) by setting
/// DestinationSubDirectory and DestinationSubPath. That is all the .NET SDK needs: it copies them
/// there for build and publish and writes "localPath" into deps.json, which the host loads from
/// (measured with .NET 10: native/&lt;rid&gt;/ in a portable build, the localPath in the trace).
/// Two files on one path stop the build (ASM0044). In a C# project for .NET 5 or later, a
/// fallback loader (see NativeLoaderSource) covers NativeAOT, which reads no deps.json.
/// </para>
/// <para>Error: ASM0061 (the file cannot be written).</para>
/// </remarks>
public sealed class AsmWritePackageConsumerTargets : Task
{
    /// <summary>The package id.</summary>
    [Required]
    public string PackageId { get; set; } = "";

    /// <summary>The package version, for the header.</summary>
    public string PackageVersion { get; set; } = "";

    /// <summary>The SDK that writes it, for the header ("AsmFoundation.Sdk 0.3.0").</summary>
    public string Origin { get; set; } = "AsmFoundation.Sdk";

    /// <summary>The library names the package's native files derive from (AssemblyNames: calc for calc.dll, libcalc.so, libcalc.dylib).</summary>
    [Required]
    public ITaskItem[] LibraryNames { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The .targets file to write (written only when its text changes).</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Writes the targets.</summary>
    /// <returns>True on success; false after logging ASM0061.</returns>
    public override bool Execute()
    {
        try
        {
            // Inside the try: Generate refuses a library name it cannot put in the loader's C#
            // (ArgumentException), which must end as ASM0061, not as an unhandled task failure.
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
            Log.LogError(null, "ASM0061", null, OutputPath, 0, 0, 0, 0, "Cannot write the package targets {0}: {1}", OutputPath, ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>The targets text.</summary>
    /// <returns>The MSBuild project, "\n" line ends.</returns>
    /// <exception cref="ArgumentException">A library name holds a quote or a backslash (see <see cref="NativeLoaderSource.Generate"/>).</exception>
    internal string Generate()
    {
        string s = Sanitize(PackageId);
        string id = Xml(PackageId);
        string[] names = LibraryNames.Select(n => n.ItemSpec).Where(n => n.Length != 0).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // The loader's C# lines, one MSBuild item each. The folder is the consumer's choice, known
        // only in its build: a marker replaced by the property AFTER escaping, so it expands.
        const string folderMarker = "@@ASMSDK_FOLDER@@";
        string loader = NativeLoaderSource.Generate("AsmNativeLibraryLoader_" + s, names.Select(n => new KeyValuePair<string, string>(n, folderMarker)), $"{Origin} for the package {PackageId}");
        string lines = string.Join(";", loader.Split('\n').Where(l => l.Length != 0).Select(EscapeLine))
            .Replace(EscapeText(folderMarker), $"$(_AsmPackageFolder_{s})");

        var x = new StringBuilder();
        x.Append("<Project>\n");
        x.Append("  <!--\n");
        x.Append($"    Written by {Xml(Origin)} for the package {id} {Xml(PackageVersion)}: where the package's native\n");
        x.Append("    libraries go in the output of a .NET project that references it. Set BinariesCopyPath on the\n");
        x.Append("    PackageReference, or as the project's property: Runtimes (runtimes/{rid}/native), AppFolder,\n");
        x.Append("    RidFolders ({rid}), Auto, or a folder such as native/{rid}. Without it, NuGet's own layout\n");
        x.Append("    stays and nothing here acts. Everything is named for this package only.\n");
        x.Append("  -->\n\n");

        // The value and its folder: properties only, so the loader target can use them before
        // ResolvePackageAssets has run.
        x.Append($"  <Target Name=\"_AsmPackageLayout_{s}\" Condition=\"'$(UsingAsmFoundationSdk)' != 'true'\">\n");
        x.Append("    <ItemGroup>\n");
        x.Append($"      <_AsmPackageReference_{s} Include=\"@(PackageReference)\" Condition=\"'%(Identity)' == '{id}'\" />\n");
        x.Append("    </ItemGroup>\n");
        x.Append("    <PropertyGroup>\n");
        x.Append($"      <_AsmPackageValue_{s}>@(_AsmPackageReference_{s}->'%(BinariesCopyPath)')</_AsmPackageValue_{s}>\n");
        x.Append($"      <_AsmPackageValue_{s} Condition=\"'$(_AsmPackageValue_{s})' == ''\">$(BinariesCopyPath)</_AsmPackageValue_{s}>\n");
        x.Append($"      <_AsmPackageValue_{s}>$(_AsmPackageValue_{s}.Trim())</_AsmPackageValue_{s}>\n");
        x.Append($"      <_AsmPackageActive_{s} Condition=\"'$(_AsmPackageValue_{s})' != '' and '$(_AsmPackageValue_{s})' != 'Auto'\">true</_AsmPackageActive_{s}>\n");
        // Absolute paths, "..", characters no OS takes, a placeholder other than {rid}.
        x.Append($"      <_AsmPackageInvalid_{s} Condition=\"'$(_AsmPackageActive_{s})' == 'true' and $([System.Text.RegularExpressions.Regex]::IsMatch('$(_AsmPackageValue_{s})', '(?i)^[\\\\/]|^[a-z]:|(^|[\\\\/])\\.\\.([\\\\/]|$)|[&lt;&gt;:&quot;|?*]|\\{{(?!rid\\}})|(?&lt;!\\{{rid)\\}}'))\">true</_AsmPackageInvalid_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s}>$(_AsmPackageValue_{s}.Replace('\\', '/'))</_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s} Condition=\"'$(_AsmPackageValue_{s})' == 'Runtimes'\">runtimes/{{rid}}/native</_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s} Condition=\"'$(_AsmPackageValue_{s})' == 'RidFolders'\">{{rid}}</_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s} Condition=\"'$(_AsmPackageValue_{s})' == 'AppFolder'\"></_AsmPackageFolder_{s}>\n");
        // "./a//b/" means a/b on every OS; {RID} means {rid}.
        x.Append($"      <_AsmPackageFolder_{s}>$([System.Text.RegularExpressions.Regex]::Replace('/$(_AsmPackageFolder_{s})/', '/(\\./)+', '/'))</_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s}>$([System.Text.RegularExpressions.Regex]::Replace('$(_AsmPackageFolder_{s})', '/{{2,}}', '/').Trim('/'))</_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s}>$([System.Text.RegularExpressions.Regex]::Replace('$(_AsmPackageFolder_{s})', '(?i)\\{{rid\\}}', '{{rid}}'))</_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageFolder_{s} Condition=\"'$(_AsmPackageFolder_{s})' == '.'\"></_AsmPackageFolder_{s}>\n");
        x.Append($"      <_AsmPackageDirectory_{s}></_AsmPackageDirectory_{s}>\n");
        x.Append($"      <_AsmPackageDirectory_{s} Condition=\"'$(_AsmPackageFolder_{s})' != ''\">$(_AsmPackageFolder_{s})/</_AsmPackageDirectory_{s}>\n");
        x.Append("    </PropertyGroup>\n");
        x.Append($"    <Error Code=\"ASM0043\" Condition=\"'$(_AsmPackageInvalid_{s})' == 'true'\" Text=\"BinariesCopyPath '$(_AsmPackageValue_{s})' for the package {id} cannot be used. Use Runtimes, AppFolder, RidFolders, Auto, or a folder inside the output: no absolute path, no .., none of &lt; &gt; : &quot; | ? *, and no placeholder but {{rid}}.\" />\n");
        x.Append("  </Target>\n\n");

        // The move: after NuGet resolved the package's assets, before anything copies them.
        x.Append($"  <Target Name=\"_AsmPackageBinariesCopyPath_{s}\" AfterTargets=\"ResolvePackageAssets\" DependsOnTargets=\"_AsmPackageLayout_{s}\" Condition=\"'$(UsingAsmFoundationSdk)' != 'true'\">\n");
        x.Append($"    <ItemGroup Condition=\"'$(_AsmPackageActive_{s})' == 'true' and '$(_AsmPackageInvalid_{s})' != 'true'\">\n");
        // Every metadata reference is qualified: an unqualified %(DestinationSubDirectory) batches
        // over the source list, and NuGet's NativeCopyLocalItems of a RID-specific build define
        // none (MSB4096, measured). So each path is computed from the source item alone.
        string portableDirectory = $"$([System.String]::Copy('$(_AsmPackageDirectory_{s})').Replace('{{rid}}', '%(RuntimeTargetsCopyLocalItems.RuntimeIdentifier)'))";
        string ridDirectory = $"$([System.String]::Copy('$(_AsmPackageDirectory_{s})').Replace('{{rid}}', $([System.Text.RegularExpressions.Regex]::Replace('%(NativeCopyLocalItems.PathInPackage)', '^runtimes/([^/]+)/.*$', '$1'))))";
        x.Append("      <!-- A portable build: every RID's file, with its RID. -->\n");
        x.Append($"      <_AsmPackageMoved_{s} Include=\"@(RuntimeTargetsCopyLocalItems)\" Condition=\"'%(RuntimeTargetsCopyLocalItems.NuGetPackageId)' == '{id}' and '%(RuntimeTargetsCopyLocalItems.AssetType)' == 'native'\">\n");
        x.Append($"        <DestinationSubDirectory>{portableDirectory}</DestinationSubDirectory>\n");
        x.Append($"        <DestinationSubPath>{portableDirectory}%(RuntimeTargetsCopyLocalItems.Filename)%(RuntimeTargetsCopyLocalItems.Extension)</DestinationSubPath>\n");
        x.Append($"      </_AsmPackageMoved_{s}>\n");
        x.Append($"      <RuntimeTargetsCopyLocalItems Remove=\"@(_AsmPackageMoved_{s})\" />\n");
        x.Append($"      <RuntimeTargetsCopyLocalItems Include=\"@(_AsmPackageMoved_{s})\" />\n");
        x.Append("      <!-- A RID-specific build: NuGet's best match, whose RID only its path in the package names. -->\n");
        x.Append($"      <_AsmPackageMovedRid_{s} Include=\"@(NativeCopyLocalItems)\" Condition=\"'%(NativeCopyLocalItems.NuGetPackageId)' == '{id}' and $([System.String]::Copy('%(NativeCopyLocalItems.PathInPackage)').StartsWith('runtimes/'))\">\n");
        x.Append($"        <DestinationSubDirectory>{ridDirectory}</DestinationSubDirectory>\n");
        x.Append($"        <DestinationSubPath>{ridDirectory}%(NativeCopyLocalItems.Filename)%(NativeCopyLocalItems.Extension)</DestinationSubPath>\n");
        x.Append($"      </_AsmPackageMovedRid_{s}>\n");
        x.Append($"      <NativeCopyLocalItems Remove=\"@(_AsmPackageMovedRid_{s})\" />\n");
        x.Append($"      <NativeCopyLocalItems Include=\"@(_AsmPackageMovedRid_{s})\" />\n");
        x.Append($"      <_AsmPackageMovedAll_{s} Include=\"@(_AsmPackageMoved_{s});@(_AsmPackageMovedRid_{s})\" />\n");
        x.Append("    </ItemGroup>\n");
        // A folder without {rid} holds each file name once: two RIDs of one OS would overwrite.
        // Counted into properties first: an item transform's own quotes cannot sit inside a
        // quoted condition string.
        x.Append("    <PropertyGroup>\n");
        x.Append($"      <_AsmPackageMovedCount_{s}>@(_AsmPackageMovedAll_{s}->Count())</_AsmPackageMovedCount_{s}>\n");
        x.Append($"      <_AsmPackageDistinctCount_{s}>@(_AsmPackageMovedAll_{s}->'%(DestinationSubPath)'->Distinct()->Count())</_AsmPackageDistinctCount_{s}>\n");
        x.Append("    </PropertyGroup>\n");
        x.Append($"    <Error Code=\"ASM0044\" Condition=\"'$(_AsmPackageMovedCount_{s})' != '$(_AsmPackageDistinctCount_{s})'\" Text=\"BinariesCopyPath '$(_AsmPackageValue_{s})' puts two files of the package {id} on one path: @(_AsmPackageMovedAll_{s}->'%(DestinationSubPath)', ', '). Add {{rid}} to the folder (for example native/{{rid}}).\" />\n");
        x.Append("  </Target>\n\n");

        // The fallback loader, for what reads no deps.json (NativeAOT). Before BeforeCompile:
        // _GenerateCompileDependencyCache hashes @(Compile) right after it, and only that hash
        // makes CoreCompile run again when the loader leaves the list (a layout back to Auto).
        x.Append($"  <Target Name=\"_AsmPackageNativeLoader_{s}\" BeforeTargets=\"BeforeCompile\" DependsOnTargets=\"_AsmPackageLayout_{s}\"\n");
        x.Append("          Condition=\"'$(UsingAsmFoundationSdk)' != 'true' and '$(Language)' == 'C#' and '$(AsmGenerateNativeLibraryLoader)' != 'false' and '$(TargetFrameworkIdentifier)' == '.NETCoreApp' and $([MSBuild]::VersionGreaterThanOrEquals('$(TargetFrameworkVersion.TrimStart(`vV`))', '5.0'))\">\n");
        x.Append("    <PropertyGroup>\n");
        x.Append($"      <_AsmPackageLoaderPath_{s}>$(IntermediateOutputPath)AsmNativeLibraryLoader.{s}.g.cs</_AsmPackageLoaderPath_{s}>\n");
        x.Append("    </PropertyGroup>\n");
        x.Append($"    <WriteLinesToFile Condition=\"'$(_AsmPackageActive_{s})' == 'true' and '$(_AsmPackageInvalid_{s})' != 'true' and '$(_AsmPackageFolder_{s})' != ''\"\n");
        x.Append($"                      File=\"$(_AsmPackageLoaderPath_{s})\" Overwrite=\"true\" WriteOnlyWhenDifferent=\"true\"\n");
        x.Append($"                      Lines=\"{lines}\" />\n");
        x.Append($"    <ItemGroup Condition=\"'$(_AsmPackageActive_{s})' == 'true' and '$(_AsmPackageInvalid_{s})' != 'true' and '$(_AsmPackageFolder_{s})' != ''\">\n");
        x.Append($"      <Compile Include=\"$(_AsmPackageLoaderPath_{s})\" />\n");
        x.Append($"      <FileWrites Include=\"$(_AsmPackageLoaderPath_{s})\" />\n");
        x.Append("    </ItemGroup>\n");
        x.Append("  </Target>\n");
        x.Append("</Project>\n");
        return x.ToString();
    }

    /// <summary>A package id as part of an MSBuild name: letters and digits kept, everything else "_".</summary>
    /// <param name="id">The package id.</param>
    /// <returns>For example "Acme_Crc" for Acme.Crc.</returns>
    internal static string Sanitize(string id) => new string(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Text for an XML attribute or element.</summary>
    /// <param name="text">The text.</param>
    /// <returns>With &amp; &lt; &gt; &quot; escaped.</returns>
    private static string Xml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>
    /// One C# line as one item of WriteLinesToFile's Lines: MSBuild-escaped (so ; $ @ % ' * ?
    /// stay literal and no item splits), leading spaces as %20 (MSBuild trims each item, which
    /// would lose the indentation), then XML-escaped for the attribute.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The item text.</returns>
    private static string EscapeLine(string line)
    {
        int indent = line.Length - line.TrimStart(' ').Length;
        return string.Concat(Enumerable.Repeat("%20", indent)) + EscapeText(line.Substring(indent));
    }

    /// <summary>MSBuild's escaping of the characters it reads specially, then XML escaping.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    private static string EscapeText(string text)
    {
        var escaped = new StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '%': escaped.Append("%25"); break;
                case '$': escaped.Append("%24"); break;
                case '@': escaped.Append("%40"); break;
                case ';': escaped.Append("%3B"); break;
                case '\'': escaped.Append("%27"); break;
                case '*': escaped.Append("%2A"); break;
                case '?': escaped.Append("%3F"); break;
                default: escaped.Append(c); break;
            }
        }

        return Xml(escaped.ToString());
    }
}
