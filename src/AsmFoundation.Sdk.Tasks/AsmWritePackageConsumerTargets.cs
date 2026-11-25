using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmFoundation.Sdk.Bindings;
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

    /// <summary>
    /// The library names the package's native files derive from (AssemblyNames: calc for calc.dll,
    /// libcalc.so, libcalc.dylib), for the fallback loader. Empty for a package that carries asm
    /// programs only: the targets then move the files and write no loader (a program is started,
    /// never loaded through [DllImport]).
    /// </summary>
    public ITaskItem[] LibraryNames { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// Whether the package holds runtimes/ files for the BinariesCopyPath targets to move. False for
    /// a package whose .NET consumers link it instead (WebAssembly archives) and that is here for
    /// its C# bindings only: the move and the loader are then left out.
    /// </summary>
    public bool MovesNativeFiles { get; set; } = true;

    /// <summary>The asm library whose C# bindings the package carries (its import name), or empty for none: the bindings targets are written only with it.</summary>
    public string BindingsLibrary { get; set; } = "";

    /// <summary>The bindings' namespace when the consumer names none (the library's name in PascalCase).</summary>
    public string BindingsNamespace { get; set; } = "";

    /// <summary>The prefix of the baseline's NuGet tokens (see AsmWritePackageBindings.TokenPrefixOf).</summary>
    public string BindingsTokenPrefix { get; set; } = "";

    /// <summary>The API file's path in the package (build/bindings/&lt;library&gt;.api.json): what the generator reads.</summary>
    public string BindingsApiFile { get; set; } = "";

    /// <summary>The bindings generator's path in the package (build/bindings/AsmFoundation.Sdk.Bindings.dll).</summary>
    public string BindingsGenerator { get; set; } = "";

    /// <summary>The generator's version (the SDK's): the shared pick runs the newest among the packages' and the SDK's.</summary>
    public string BindingsGeneratorVersion { get; set; } = "";

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
        // None for a package of programs only (see LibraryNames).
        const string folderMarker = "@@ASMSDK_FOLDER@@";
        string lines = names.Length == 0 ? "" : string.Join(";",
                NativeLoaderSource.Generate("AsmNativeLibraryLoader_" + s, names.Select(n => new KeyValuePair<string, string>(n, folderMarker)), $"{Origin} for the package {PackageId}")
                    .Split('\n').Where(l => l.Length != 0).Select(EscapeLine))
            .Replace(EscapeText(folderMarker), $"$(_AsmPackageFolder_{s})");

        var x = new StringBuilder();
        x.Append("<Project>\n");
        x.Append("  <!--\n");
        if (MovesNativeFiles)
        {
            x.Append($"    Written by {Xml(Origin)} for the package {id} {Xml(PackageVersion)}: where the package's native\n");
            x.Append("    libraries go in the output of a .NET project that references it. Set BinariesCopyPath on the\n");
            x.Append("    PackageReference, or as the project's property: Runtimes (runtimes/{rid}/native), AppFolder,\n");
            x.Append("    RidFolders ({rid}), Auto, or a folder such as native/{rid}. Without it, NuGet's own layout\n");
            x.Append("    stays and nothing here acts. Everything is named for this package only.\n");
        }
        else
        {
            x.Append($"    Written by {Xml(Origin)} for the package {id} {Xml(PackageVersion)}: the C# bindings of its asm\n");
            x.Append("    library (below). Its .NET consumers link it rather than load it, so no file is moved here.\n");
        }

        x.Append("  -->\n\n");
        if (!MovesNativeFiles)
        {
            AppendBindings(x, s, id);
            x.Append("</Project>\n");
            return x.ToString();
        }

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
        // A package of programs only has no library to load, and gets no loader target.
        if (names.Length != 0)
        {
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
        }

        AppendBindings(x, s, id);
        x.Append("</Project>\n");
        return x.ToString();
    }

    /// <summary>
    /// The settings of a package's bindings, in the order the SDK's shape rules read them
    /// (Shapes.Resolve), each with its AsmBindings item metadata, its project property, its
    /// default and its values (null for true/false).
    /// </summary>
    private static readonly (string Name, string Property, string Default, string[]? Values)[] BindingsSettings =
    {
        ("Nesting", "AsmBindingsNesting", "Files", ShapeSettings.NestingNames),
        ("SpanOverloads", "AsmBindingsSpanOverloads", "true", null),
        ("Visibility", "AsmBindingsVisibility", "Internal", ShapeSettings.VisibilityNames),
        ("ClassKind", "AsmBindingsClassKind", "Static", ShapeSettings.ClassKindNames),
        ("Virtual", "AsmBindingsVirtual", "false", null),
        ("Sealed", "AsmBindingsSealed", "false", null),
        ("DefaultBindings", "AsmDefaultBindings", "true", null),
    };

    /// <summary>
    /// Appends the targets of the package's C# bindings, when it has them (<see cref="BindingsLibrary"/>):
    /// the tokens of its baseline, computed and checked from the consumer's settings; the switch
    /// to the generator; and the shared targets.
    /// </summary>
    /// <param name="x">Where to write.</param>
    /// <param name="s">The package id as part of an MSBuild name.</param>
    /// <param name="id">The package id, XML-escaped.</param>
    /// <remarks>
    /// <para>
    /// Every check and every message is the SDK's (the same shape rules, Shapes, compiled into this
    /// task: each combination's message is taken from them, not retyped), but run by MSBuild alone:
    /// the package does not depend on AsmFoundation.Sdk. The precedence is the SDK's too, as far as
    /// a written baseline goes (an attribute turns the generator on, which reads the rest): the
    /// project's &lt;AsmBindings Include="library" /&gt; item, then the AsmBindings* property, then
    /// the default. The first error stops the build, so two packages never repeat one message.
    /// </para>
    /// <para>
    /// The timing: NuGet preprocesses the content files in RunProduceContentAssets, which the .NET
    /// SDK runs before AssignProjectConfiguration, long before ResolveProjectReferences; the tokens
    /// are filled just before it, and the switch looked at just after it, when the baseline is a
    /// Compile item that can be taken out again.
    /// </para>
    /// </remarks>
    private void AppendBindings(StringBuilder x, string s, string id)
    {
        if (BindingsLibrary.Length == 0)
        {
            return;
        }

        string library = Xml(BindingsLibrary);
        string p = BindingsTokenPrefix;
        // The bindings live where a .NET consumer can see them: C#, .NET 7 or later (the
        // contentFiles folder is net7.0, which NuGet gives no older framework, so the gate only
        // spares the others the work). Never in an asm project, which imports these targets too.
        const string gate = "'$(UsingAsmFoundationSdk)' != 'true' and '$(Language)' == 'C#'";
        const string active = "'$(AsmGenerateBindings)' != 'false' and '$(TargetFrameworkIdentifier)' == '.NETCoreApp' and $([MSBuild]::VersionGreaterThanOrEquals('$(TargetFrameworkVersion.TrimStart(`vV`))', '7.0'))";

        x.Append("\n  <!--\n");
        x.Append($"    The C# bindings of the asm library {library}: its baseline, contentFiles/cs/net7.0/AsmBindings/{library}.cs.pp,\n");
        x.Append("    which NuGet compiles into every C# project for .NET 7 or later that references this package,\n");
        x.Append($"    its $tokens$ filled from the project's settings by _AsmPackageBindingsTokens_{s}: an\n");
        x.Append($"    <AsmBindings Include=\"{library}\" /> item (Namespace, Nesting, SpanOverloads, Visibility, ClassKind,\n");
        x.Append("    Virtual, Sealed, DefaultBindings), else the properties AsmBindingsNesting, AsmBindingsSpanOverloads,\n");
        x.Append("    AsmBindingsVisibility, AsmBindingsClassKind, AsmBindingsVirtual, AsmBindingsSealed and\n");
        x.Append("    AsmDefaultBindings. When the project's code names the bindings attribute, the newest bindings\n");
        x.Append("    generator among the packages' and AsmFoundation.Sdk's takes over, reading the API file\n");
        x.Append($"    (build/bindings/{library}.api.json), and the written baseline steps aside (_AsmPackageBindings_{s}).\n");
        x.Append("    AsmGenerateBindings=false removes the bindings.\n");
        x.Append("  -->\n");
        // Visible to the generator: the item's settings travel on the API file, the properties as
        // they are. Duplicate metadata entries are harmless (the editorconfig step batches them by
        // name); duplicate properties are excluded, the SDK's own included.
        x.Append($"  <ItemGroup Condition=\"{gate}\">\n");
        x.Append("    <CompilerVisibleItemMetadata Include=\"AdditionalFiles\" MetadataName=\"AsmApiFile\" />\n");
        foreach (string name in ShapeSettings.ItemMetadataNames)
        {
            x.Append($"    <CompilerVisibleItemMetadata Include=\"AdditionalFiles\" MetadataName=\"AsmBindings{name}\" />\n");
        }

        foreach ((string _, string property, string _, string[]? _) in BindingsSettings)
        {
            x.Append($"    <CompilerVisibleProperty Include=\"{property}\" Exclude=\"@(CompilerVisibleProperty)\" />\n");
        }

        x.Append("  </ItemGroup>\n\n");

        // The tokens: every setting resolved (item, property, default), checked, then handed to
        // NuGet as PreprocessorValue items. Filled even when the bindings are off or a value is
        // wrong (with the defaults), so the content file always preprocesses.
        x.Append($"  <Target Name=\"_AsmPackageBindingsTokens_{s}\" BeforeTargets=\"RunProduceContentAssets\" Condition=\"{gate}\">\n");
        x.Append("    <ItemGroup>\n");
        x.Append($"      <_AsmBindingsItem_{s} Include=\"@(AsmBindings)\" Condition=\"'%(Identity)' == '{library}'\" />\n");
        x.Append("    </ItemGroup>\n");
        x.Append("    <PropertyGroup>\n");
        x.Append($"      <_AsmBindingsActive_{s} Condition=\"{active}\">true</_AsmBindingsActive_{s}>\n");
        x.Append($"      <_AsmBindingsNamespace_{s}>@(_AsmBindingsItem_{s}->'%(Namespace)')</_AsmBindingsNamespace_{s}>\n");
        x.Append($"      <_AsmBindingsNamespace_{s}>$(_AsmBindingsNamespace_{s}.Trim())</_AsmBindingsNamespace_{s}>\n");
        x.Append($"      <_AsmBindingsNamespace_{s} Condition=\"'$(_AsmBindingsNamespace_{s})' == ''\">{Xml(BindingsNamespace)}</_AsmBindingsNamespace_{s}>\n");
        foreach ((string name, string property, string _, string[]? values) in BindingsSettings)
        {
            string text = $"_AsmBindings{name}Text_{s}";
            string value = $"_AsmBindings{name}_{s}";
            string itemProblem = $"_AsmBindings{name}ItemProblem_{s}";
            string propertyText = $"_AsmBindings{name}PropertyText_{s}";
            string propertyProblem = $"_AsmBindings{name}PropertyProblem_{s}";
            // The item's value first; a value that is no option's is the item's problem, and
            // then, as in the SDK, the property is read as if the item said nothing.
            x.Append($"      <{text}>@(_AsmBindingsItem_{s}->'%({name})')</{text}>\n");
            x.Append($"      <{text}>$({text}.Trim())</{text}>\n");
            x.Append($"      <{value}></{value}>\n");
            AppendCanonical(x, value, text, values, onlyIfEmpty: false);
            string itemMessage = values is null
                ? $"The AsmBindings item of {BindingsLibrary}: {name} is '$({text})', which is not true or false"
                : $"The AsmBindings item of {BindingsLibrary}: {name} is '$({text})', which is not one of {string.Join(", ", values)}";
            x.Append($"      <{itemProblem} Condition=\"'$({text})' != '' and '$({value})' == ''\">{EscapeMessage(itemMessage)}</{itemProblem}>\n");
            x.Append($"      <{propertyText} Condition=\"'$({value})' == ''\">$({property}.Trim())</{propertyText}>\n");
            AppendCanonical(x, value, propertyText, values, onlyIfEmpty: true);
            string propertyMessage = values is null
                ? $"{property} is '$({propertyText})', which is not true or false"
                : $"{property} is '$({propertyText})', which is not one of {string.Join(", ", values)}";
            x.Append($"      <{propertyProblem} Condition=\"'$({propertyText})' != '' and '$({value})' == ''\">{EscapeMessage(propertyMessage)}</{propertyProblem}>\n");
        }

        x.Append("    </PropertyGroup>\n");
        foreach ((string name, string _, string _, string[]? _) in BindingsSettings)
        {
            x.Append($"    <Error Code=\"ASM0146\" Condition=\"'$(_AsmBindingsActive_{s})' == 'true' and '$(_AsmBindings{name}ItemProblem_{s})' != ''\" Text=\"$(_AsmBindings{name}ItemProblem_{s})\" />\n");
            x.Append($"    <Error Code=\"ASM0146\" Condition=\"'$(_AsmBindingsActive_{s})' == 'true' and '$(_AsmBindings{name}PropertyProblem_{s})' != ''\" Text=\"$(_AsmBindings{name}PropertyProblem_{s})\" />\n");
        }

        // The defaults, before the combinations are judged (as the SDK's rules resolve every
        // option first: Virtual on an unset ClassKind is Virtual on Static). A wrong value reached
        // only an inactive project (no error above), whose baseline is never compiled; the
        // default keeps its tokens well-formed anyway.
        x.Append("    <PropertyGroup>\n");
        foreach ((string name, string _, string defaultValue, string[]? _) in BindingsSettings)
        {
            x.Append($"      <_AsmBindings{name}_{s} Condition=\"'$(_AsmBindings{name}_{s})' == ''\">{defaultValue}</_AsmBindings{name}_{s}>\n");
        }

        x.Append("    </PropertyGroup>\n");

        // What C# cannot do with a shape: the SDK's own messages, one per combination.
        string what = BindingsLibrary + "'s baseline";
        var combinations = new (string Condition, string Message)[]
        {
            ($"'$(_AsmBindingsClassKind_{s})' == 'Static' and '$(_AsmBindingsVisibility_{s})' == 'ProtectedInternal'", Shapes.Problems(what, instance: false, visibility: 2, isVirtual: false, isSealed: false).Single()),
            ($"'$(_AsmBindingsClassKind_{s})' == 'Static' and '$(_AsmBindingsVirtual_{s})' == 'true'", Shapes.Problems(what, instance: false, visibility: 0, isVirtual: true, isSealed: false).Single()),
            ($"'$(_AsmBindingsClassKind_{s})' == 'Static' and '$(_AsmBindingsSealed_{s})' == 'true'", Shapes.Problems(what, instance: false, visibility: 0, isVirtual: false, isSealed: true).Single()),
            ($"'$(_AsmBindingsVirtual_{s})' == 'true' and '$(_AsmBindingsSealed_{s})' == 'true'", Shapes.Problems(what, instance: true, visibility: 0, isVirtual: true, isSealed: true).Single()),
        };
        foreach ((string condition, string message) in combinations)
        {
            x.Append($"    <Error Code=\"ASM0146\" Condition=\"'$(_AsmBindingsActive_{s})' == 'true' and {condition}\" Text=\"{EscapeMessage(message)}\" />\n");
        }

        // The namespace: dotted C# identifiers, as the SDK checks it (letters or _, then letters,
        // digits or _). The message keeps the SDK's words; the namespace is inserted by MSBuild.
        x.Append($"    <Error Code=\"ASM0146\" Condition=\"'$(_AsmBindingsActive_{s})' == 'true' and !$([System.Text.RegularExpressions.Regex]::IsMatch('$(_AsmBindingsNamespace_{s})', '^[\\p{{L}}_][\\p{{L}}\\p{{Nd}}_]*(\\.[\\p{{L}}_][\\p{{L}}\\p{{Nd}}_]*)*$'))\"\n");
        x.Append($"           Text=\"{EscapeMessage(BindingsLibrary + "'s baseline namespace '")}$(_AsmBindingsNamespace_{s}){EscapeMessage("' is not a C# namespace name")}\" />\n");

        // The values the tokens take, from the resolved settings.
        x.Append("    <PropertyGroup>\n");
        x.Append($"      <_AsmBindingsAccess_{s}>internal</_AsmBindingsAccess_{s}>\n");
        x.Append($"      <_AsmBindingsAccess_{s} Condition=\"'$(_AsmBindingsVisibility_{s})' == 'Public'\">public</_AsmBindingsAccess_{s}>\n");
        x.Append($"      <_AsmBindingsAccess_{s} Condition=\"'$(_AsmBindingsVisibility_{s})' == 'ProtectedInternal'\">protected internal</_AsmBindingsAccess_{s}>\n");
        // Namespace-level types are public or internal: a protected internal member's class is public.
        x.Append($"      <_AsmBindingsTypeAccess_{s}>public</_AsmBindingsTypeAccess_{s}>\n");
        x.Append($"      <_AsmBindingsTypeAccess_{s} Condition=\"'$(_AsmBindingsVisibility_{s})' == 'Internal'\">internal</_AsmBindingsTypeAccess_{s}>\n");
        x.Append($"      <_AsmBindingsMembers_{s}>$(_AsmBindingsAccess_{s})</_AsmBindingsMembers_{s}>\n");
        x.Append($"      <_AsmBindingsMembers_{s} Condition=\"'$(_AsmBindingsVirtual_{s})' == 'true'\">$(_AsmBindingsAccess_{s}) virtual</_AsmBindingsMembers_{s}>\n");
        x.Append($"      <_AsmBindingsClassModifiers_{s}>$(_AsmBindingsTypeAccess_{s})</_AsmBindingsClassModifiers_{s}>\n");
        x.Append($"      <_AsmBindingsClassModifiers_{s} Condition=\"'$(_AsmBindingsSealed_{s})' == 'true'\">$(_AsmBindingsTypeAccess_{s}) sealed</_AsmBindingsClassModifiers_{s}>\n");
        x.Append("    </PropertyGroup>\n");
        x.Append("    <ItemGroup>\n");
        var tokens = new (string Suffix, string Value)[]
        {
            ("namespace", $"$(_AsmBindingsNamespace_{s})"),
            ("nesting", $"$(_AsmBindingsNesting_{s})"),
            ("classkind", $"$(_AsmBindingsClassKind_{s})"),
            ("spanoverloads", $"$(_AsmBindingsSpanOverloads_{s})"),
            ("functions", $"$(_AsmBindingsDefaultBindings_{s})"),
            ("access", $"$(_AsmBindingsAccess_{s})"),
            ("typeaccess", $"$(_AsmBindingsTypeAccess_{s})"),
            ("members", $"$(_AsmBindingsMembers_{s})"),
            ("classmodifiers", $"$(_AsmBindingsClassModifiers_{s})"),
        };
        foreach ((string suffix, string value) in tokens)
        {
            x.Append($"      <PreprocessorValue Include=\"{p}{suffix}\" Value=\"{value}\" />\n");
        }

        x.Append("    </ItemGroup>\n");
        x.Append("  </Target>\n\n");

        // The switch: the baseline NuGet made a Compile item is this package's; with the
        // generator on (or the bindings off) it steps aside, and with the generator on the API
        // file and this package's generator go to the compiler instead.
        string apiFile = $"$([MSBuild]::NormalizePath('$(MSBuildThisFileDirectory)', '..', '..', '{Xml(BindingsApiFile)}'))";
        string generator = $"$([MSBuild]::NormalizePath('$(MSBuildThisFileDirectory)', '..', '..', '{Xml(BindingsGenerator)}'))";
        x.Append($"  <Target Name=\"_AsmPackageBindings_{s}\" AfterTargets=\"RunProduceContentAssets\" DependsOnTargets=\"_AsmBindingsSwitch\" Condition=\"{gate}\">\n");
        x.Append("    <ItemGroup>\n");
        x.Append($"      <_AsmPackageBindingsSource_{s} Include=\"@(Compile)\" Condition=\"'%(Compile.NuGetPackageId)' == '{id}' and '%(Compile.Filename)%(Compile.Extension)' == '{library}.cs'\" />\n");
        x.Append("    </ItemGroup>\n");
        x.Append("    <PropertyGroup>\n");
        x.Append($"      <_AsmPackageBindingsOn_{s} Condition=\"'@(_AsmPackageBindingsSource_{s})' != '' and '$(AsmGenerateBindings)' != 'false'\">true</_AsmPackageBindingsOn_{s}>\n");
        x.Append("    </PropertyGroup>\n");
        x.Append($"    <ItemGroup Condition=\"'@(_AsmPackageBindingsSource_{s})' != '' and ('$(_AsmPackageBindingsOn_{s})' != 'true' or '$(_AsmBindingsGenerate)' == 'true')\">\n");
        x.Append($"      <Compile Remove=\"@(_AsmPackageBindingsSource_{s})\" />\n");
        x.Append("    </ItemGroup>\n");
        x.Append($"    <ItemGroup Condition=\"'$(_AsmPackageBindingsOn_{s})' == 'true'\">\n");
        x.Append($"      <_AsmBindingsLibrary Include=\"{library}\" />\n");
        x.Append("    </ItemGroup>\n");
        x.Append($"    <ItemGroup Condition=\"'$(_AsmPackageBindingsOn_{s})' == 'true' and '$(_AsmBindingsGenerate)' == 'true'\">\n");
        x.Append($"      <AdditionalFiles Include=\"{apiFile}\" AsmApiFile=\"true\"");
        foreach (string name in ShapeSettings.ItemMetadataNames)
        {
            x.Append($"\n                       AsmBindings{name}=\"@(_AsmBindingsItem_{s}->'%({name})')\"");
        }

        x.Append(" />\n");
        x.Append($"      <_AsmBindingsGeneratorCandidate Include=\"{generator}\" Version=\"{Xml(BindingsGeneratorVersion)}\" />\n");
        x.Append("    </ItemGroup>\n");
        // Unsafe code: the pointer parameters and the string wrappers' buffers. Set, not
        // defaulted: the .NET SDK has already set it false (measured with the SDK's own targets).
        x.Append($"    <PropertyGroup Condition=\"'$(_AsmPackageBindingsOn_{s})' == 'true'\">\n");
        x.Append("      <AllowUnsafeBlocks>true</AllowUnsafeBlocks>\n");
        x.Append("    </PropertyGroup>\n");
        x.Append("  </Target>\n\n");
        x.Append(SharedBindingsTargets);
    }

    /// <summary>Appends the properties that set a setting's canonical value from a text, case-insensitively (MSBuild compares strings so).</summary>
    /// <param name="x">Where to write.</param>
    /// <param name="value">The property that receives the value.</param>
    /// <param name="text">The property holding the text.</param>
    /// <param name="values">The setting's values, or null for true/false.</param>
    /// <param name="onlyIfEmpty">Whether to set it only when no value is set yet (the property after the item).</param>
    private static void AppendCanonical(StringBuilder x, string value, string text, string[]? values, bool onlyIfEmpty)
    {
        string guard = onlyIfEmpty ? $"'$({value})' == '' and " : "";
        foreach (string option in values ?? new[] { "true", "false" })
        {
            x.Append($"      <{value} Condition=\"{guard}'$({text})' == '{option}'\">{option}</{value}>\n");
        }
    }

    /// <summary>A message as the text of an MSBuild element or attribute: MSBuild's special characters escaped except $( ) of the properties it names, then XML.</summary>
    /// <param name="message">The message, which may name properties as $(Name).</param>
    /// <returns>The escaped text.</returns>
    /// <remarks>Only ' and ; and % and @ are escaped (the SDK's messages hold no * or ?): the $(…) references must stay expandable.</remarks>
    private static string EscapeMessage(string message) =>
        Xml(message.Replace("%", "%25").Replace("@", "%40").Replace(";", "%3B").Replace("'", "%27"));

    /// <summary>
    /// The C# bindings' shared targets, written word for word into every asm package with bindings
    /// and kept identical in AsmFoundation.Sdk's consumer targets (a test compares them). MSBuild
    /// keeps the last definition of a target name, so whichever copy wins must do exactly this:
    /// THEIR NAMES AND MEANINGS NEVER CHANGE.
    /// </summary>
    internal const string SharedBindingsTargets =
        "  <!--\n" +
        "    The C# bindings' shared targets. AsmFoundation.Sdk's consumer targets and every asm package\n" +
        "    with bindings carry these three, word for word, and MSBuild keeps the last definition it\n" +
        "    imports: THEIR NAMES AND MEANINGS NEVER CHANGE.\n" +
        "    _AsmBindingsSwitch: _AsmBindingsGenerate is true when a Compile item says AsmLibrary, the\n" +
        "    attribute's name (a word search, no compiler; a missing file is skipped): the bindings\n" +
        "    generator then writes the bindings, and the written baselines step aside.\n" +
        "    _AsmCheckBindingsItems: ASM0140 for each AsmBindings item that names none of the libraries\n" +
        "    the project has bindings for (the _AsmBindingsLibrary items the SDK and the packages add).\n" +
        "    _AsmPickBindingsGenerator: the newest generator candidate (_AsmBindingsGeneratorCandidate,\n" +
        "    with Version) becomes the one Analyzer: two would each write every library's bindings\n" +
        "    (CS0101, CS0102, measured). A cross join, not a running maximum: a batched property never\n" +
        "    sees its value from the previous batch (measured).\n" +
        "  -->\n" +
        "  <Target Name=\"_AsmBindingsSwitch\">\n" +
        "    <PropertyGroup>\n" +
        "      <_AsmBindingsGenerate Condition=\"Exists('%(Compile.FullPath)') and $([System.IO.File]::ReadAllText('%(Compile.FullPath)').Contains('AsmLibrary'))\">true</_AsmBindingsGenerate>\n" +
        "    </PropertyGroup>\n" +
        "  </Target>\n" +
        "  <Target Name=\"_AsmCheckBindingsItems\" BeforeTargets=\"CoreCompile\"\n" +
        "          Condition=\"'@(AsmBindings)' != '' and '$(AsmGenerateBindings)' != 'false' and '$(UsingAsmFoundationSdk)' != 'true' and '$(Language)' == 'C#' and '$(TargetFrameworkIdentifier)' == '.NETCoreApp' and $([MSBuild]::VersionGreaterThanOrEquals('$(TargetFrameworkVersion.TrimStart(`vV`))', '7.0'))\">\n" +
        "    <ItemGroup>\n" +
        "      <_AsmBindingsCheck Include=\"@(AsmBindings)\" Known=\";@(_AsmBindingsLibrary);\" />\n" +
        "      <_AsmBindingsCheck Known=\"$([System.String]::Copy('%(Known)').ToLowerInvariant())\" Name=\";$([System.String]::Copy('%(Identity)').ToLowerInvariant());\" />\n" +
        "      <_AsmBindingsUnknown Include=\"@(_AsmBindingsCheck)\" Condition=\"!$([System.String]::Copy('%(Known)').Contains('%(Name)'))\" />\n" +
        "    </ItemGroup>\n" +
        "    <PropertyGroup>\n" +
        "      <_AsmBindingsKnownList>@(_AsmBindingsLibrary->Distinct(), ', ')</_AsmBindingsKnownList>\n" +
        "      <_AsmBindingsKnownList Condition=\"'$(_AsmBindingsKnownList)' == ''\">none</_AsmBindingsKnownList>\n" +
        "    </PropertyGroup>\n" +
        "    <Error Code=\"ASM0140\" Condition=\"'@(_AsmBindingsUnknown)' != ''\" Text=\"&lt;AsmBindings Include=&quot;%(_AsmBindingsUnknown.Identity)&quot; /&gt; names no asm library this project references with PROTO or STRUCT lines%3B it references $(_AsmBindingsKnownList)\" />\n" +
        "    <ItemGroup>\n" +
        "      <_AsmBindingsCheck Remove=\"@(_AsmBindingsCheck)\" />\n" +
        "      <_AsmBindingsUnknown Remove=\"@(_AsmBindingsUnknown)\" />\n" +
        "    </ItemGroup>\n" +
        "  </Target>\n" +
        "  <Target Name=\"_AsmPickBindingsGenerator\" BeforeTargets=\"CoreCompile\" Condition=\"'@(_AsmBindingsGeneratorCandidate)' != ''\">\n" +
        "    <ItemGroup>\n" +
        "      <_AsmGeneratorOther Include=\"@(_AsmBindingsGeneratorCandidate)\" />\n" +
        "      <_AsmGeneratorRival Include=\"@(_AsmBindingsGeneratorCandidate)\" RivalVersion=\"%(_AsmGeneratorOther.Version)\" />\n" +
        "      <_AsmGeneratorBeaten Include=\"@(_AsmGeneratorRival)\" Condition=\"$([MSBuild]::VersionGreaterThan('%(_AsmGeneratorRival.RivalVersion)', '%(_AsmGeneratorRival.Version)'))\" />\n" +
        "      <_AsmGeneratorNewest Include=\"@(_AsmBindingsGeneratorCandidate)\" Exclude=\"@(_AsmGeneratorBeaten)\" />\n" +
        "    </ItemGroup>\n" +
        "    <PropertyGroup>\n" +
        "      <_AsmPickedBindingsGenerator>%(_AsmGeneratorNewest.Identity)</_AsmPickedBindingsGenerator>\n" +
        "    </PropertyGroup>\n" +
        "    <ItemGroup>\n" +
        "      <Analyzer Include=\"$(_AsmPickedBindingsGenerator)\" />\n" +
        "      <_AsmGeneratorOther Remove=\"@(_AsmGeneratorOther)\" />\n" +
        "      <_AsmGeneratorRival Remove=\"@(_AsmGeneratorRival)\" />\n" +
        "      <_AsmGeneratorBeaten Remove=\"@(_AsmGeneratorBeaten)\" />\n" +
        "      <_AsmGeneratorNewest Remove=\"@(_AsmGeneratorNewest)\" />\n" +
        "    </ItemGroup>\n" +
        "  </Target>\n";

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
