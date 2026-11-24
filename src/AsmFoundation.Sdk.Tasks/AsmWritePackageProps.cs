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
/// Everything above is conditioned on UsingAsmFoundationSdk: a .NET consumer of the package gets
/// its runtimes/ folder from NuGet, and nothing from those items. The one exception is a .NET
/// WebAssembly app (RuntimeIdentifier browser-wasm or wasi-wasm), which links native code into
/// its own runtime and finds nothing in runtimes/ to load: for the package's wasm archives the
/// props add NativeFileReference items, without the file-named P/Invoke module (ScanForPInvokes
/// false) and with the library's own (<see cref="ImportName"/>, in .NET's _WasmPInvokeModules),
/// as the SDK's consumer targets do for a ProjectReference; a package must work without the
/// SDK. Such an app also gets the consumer targets' check: ASM0054 from a target the props
/// define, when .NET's native WebAssembly build is missing for the app's target framework (the
/// app would otherwise build without the package's code). Paths are relative to the props
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
    /// The library's name as [DllImport] spells it (the project's AssemblyName: calc for
    /// calc.dll and libcalc.so): the P/Invoke module a .NET WebAssembly app gets for the
    /// package's wasm archive. Empty writes no module name, and the app's build then names the
    /// module after the archive's file instead.
    /// </summary>
    public string ImportName { get; set; } = "";

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

        // .NET WebAssembly apps: the archive of their RID, linked into the app's runtime. Exact
        // RIDs only, no graph: browser-wasm and wasi-wasm fall back to nothing but each other's
        // parents, and a WASI archive in a browser app (or the reverse) would link and then call
        // imports the other host does not have. Evaluation-time items, like the ones above, so
        // that the wasm build sees them when it counts NativeFileReference to turn its native
        // link on (_SetWasmBuildNativeDefaults).
        foreach (ITaskItem library in LinkLibraries
                     .Where(l => IsWasmRid(l.GetMetadata("RuntimeIdentifier")))
                     .OrderBy(l => l.GetMetadata("RuntimeIdentifier"), StringComparer.Ordinal))
        {
            string rid = library.GetMetadata("RuntimeIdentifier");
            var wasmItems = new XElement("ItemGroup",
                new XAttribute("Condition", $"!({IsAsm}) and '$(RuntimeIdentifier)' == '{rid}'"),
                new XElement("NativeFileReference",
                    new XAttribute("Include", "$(MSBuildThisFileDirectory)../../" + library.ItemSpec.Replace('\\', '/')),
                    new XAttribute("ScanForPInvokes", "false"),
                    new XAttribute("AsmPackage", PackageId)));
            if (ImportName.Length != 0)
            {
                // Exclude: another package (or the consumer targets) may have named it already.
                wasmItems.Add(new XElement("_WasmPInvokeModules",
                    new XAttribute("Include", ImportName),
                    new XAttribute("Exclude", "@(_WasmPInvokeModules)")));
            }

            project.Add(wasmItems);
        }

        // The same check the SDK's consumer targets make for a ProjectReference (_AsmCheckWasmNativeBuild,
        // ASM0054): without .NET's native WebAssembly build for the app's target framework, .NET
        // imports none of its WebAssembly build targets, ignores NativeFileReference, and the app
        // builds without the package's code (measured: SDK 11.0.100 with only the net11.0
        // wasm-tools built a net10.0 app with no AppBundle and no warning). One clause per wasm RID
        // the package has: a package without one adds nothing here.
        string[] wasmRids = LinkLibraries.Select(l => l.GetMetadata("RuntimeIdentifier")).Where(IsWasmRid)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        if (wasmRids.Length != 0)
        {
            project.Add(WasmNativeBuildCheck(id, IsAsm, wasmRids));
        }

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

    /// <summary>
    /// The target that stops a .NET WebAssembly app with ASM0054 when .NET's native WebAssembly
    /// build, the only one that links the package's archive, is missing for the app's target
    /// framework, naming the workload to install.
    /// </summary>
    /// <param name="id">The property-name-safe package id, which keeps the target's name and its properties apart from other packages' checks.</param>
    /// <param name="isAsm">The condition that is true in an asm project (which links the package itself and needs no .NET workload).</param>
    /// <param name="wasmRids">The WebAssembly RIDs the package was packed for (at least one).</param>
    /// <returns>The Target element.</returns>
    /// <remarks>
    /// <para>
    /// The test is the one .NET's own imports make: UsingBrowserRuntimeWorkload (browser-wasm) and
    /// UsingWasiRuntimeWorkload (wasi-wasm) decide whether its WebAssembly build targets are
    /// imported. .NET's workload manifests set them from its verdicts for the app's target
    /// framework (WasmNativeWorkloadAvailable, WasiNativeWorkloadAvailable, computed from the
    /// installed workload packs), and a browser app .NET marks as needing the native build gets
    /// UsingBrowserRuntimeWorkload true regardless, so the switch is the test. When one is true
    /// and the workload is missing, .NET stops the build itself.
    /// </para>
    /// <para>
    /// The fix is named as the SDK's consumer targets name it (_AsmCheckWasmNativeBuild, where
    /// the measurements are): wasm-tools or wasi-experimental for an app of the SDK's own .NET;
    /// on a later SDK, the SDK of the app's own .NET (global.json) with its workload, for both
    /// runtimes. A later SDK's wasm-tools-net&lt;major&gt; did not build such a browser app
    /// (measured with 11.0.100: .NET's own targets stopped even an app with no asm code), and its
    /// WASI build serves only its own .NET.
    /// </para>
    /// <para>
    /// BeforeTargets PrepareForBuild, one of a build's first steps, so that nothing is compiled
    /// for an app that cannot work. A Target element in a props file is fine: MSBuild collects
    /// targets from every import before it runs any.
    /// </para>
    /// </remarks>
    private XElement WasmNativeBuildCheck(string id, string isAsm, IReadOnlyList<string> wasmRids)
    {
        // One clause per packed wasm RID, each with the import switch of its runtime.
        string missing = string.Join(" or ", wasmRids.Select(rid =>
            $"('$(RuntimeIdentifier)' == '{rid}' and '$({(rid.StartsWith("wasi", StringComparison.OrdinalIgnoreCase) ? "UsingWasiRuntimeWorkload" : "UsingBrowserRuntimeWorkload")})' != 'true')"));
        string workload = $"_AsmPackageWasmWorkload_{id}";
        string fix = $"_AsmPackageWasmFix_{id}";
        string appMajor = $"_AsmPackageWasmAppMajor_{id}";
        string sdkMajor = $"_AsmPackageWasmSdkMajor_{id}";
        // The app targets an earlier .NET than the SDK's (net10.0 on SDK 11). Compared through
        // properties: a property function with a quoted argument inside a quoted condition
        // string is MSB4092 ("An unexpected token"), measured in a package's props.
        string earlier = $"'$({appMajor})' != '$({sdkMajor})'";
        return new XElement("Target",
            new XAttribute("Name", $"_AsmCheckWasmNativeBuild_{id}"),
            new XAttribute("BeforeTargets", "PrepareForBuild"),
            new XAttribute("Condition", $"!({isAsm}) and ({missing})"),
            new XElement("PropertyGroup",
                new XElement(workload, new XAttribute("Condition", "'$(RuntimeIdentifier)' == 'browser-wasm'"), "wasm-tools"),
                new XElement(workload, new XAttribute("Condition", "'$(RuntimeIdentifier)' == 'wasi-wasm'"), "wasi-experimental"),
                // The app's .NET major version (10 for net10.0) and the SDK's (11 for 11.0.100,
                // and for a preview such as 11.0.100-preview.6).
                new XElement(appMajor, "$([MSBuild]::GetTargetFrameworkVersion('$(TargetFramework)', 1))"),
                new XElement(sdkMajor, "$(NETCoreSdkVersion.Split('.')[0])"),
                new XElement(fix, $"dotnet workload install $({workload})"),
                new XElement(fix,
                    new XAttribute("Condition", $"{earlier} and '$(RuntimeIdentifier)' == 'browser-wasm'"),
                    $"build it with a .NET $({appMajor}) SDK (global.json) and its $({workload}) workload, since a later SDK's $({workload})-net$({appMajor}) did not build such an app (measured with SDK 11.0.100: .NET's own targets stopped it)"),
                new XElement(fix,
                    new XAttribute("Condition", $"{earlier} and '$(RuntimeIdentifier)' == 'wasi-wasm'"),
                    $"build it with a .NET $({appMajor}) SDK (global.json) and its $({workload}) workload, since a later SDK's WASI build serves only its own .NET")),
            new XElement("Error",
                new XAttribute("Code", "ASM0054"),
                new XAttribute("Text",
                    $"$(MSBuildProjectName) links the package {PackageId} {PackageVersion} into its $(RuntimeIdentifier) runtime, which only .NET's native WebAssembly build does, " +
                    $"and this .NET SDK ($(NETCoreSdkVersion)) has none for $(TargetFramework): $({fix}). " +
                    "Without it the app would build without the package's asm code, and every call into it would fail when the app runs.")));
    }

    /// <summary>Whether a packed RID is one of the WebAssembly RIDs, whose archives a .NET app links.</summary>
    /// <param name="rid">The RID the library was packed for.</param>
    /// <returns>True for browser-wasm and wasi-wasm.</returns>
    private static bool IsWasmRid(string rid) =>
        RuntimeTarget.All.TryGetValue(rid, out RuntimeTarget? target) && target.Architecture == TargetArchitecture.Wasm;
}
