using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Decides, for each asm library a .NET project deploys, where its files go (BinariesCopyPath):
/// the project's own reference to it, then the project's BinariesCopyPath property, then the
/// choice of the .NET library it comes through, then the SDK's default.
/// </summary>
/// <remarks>
/// <para>
/// The order puts the project that deploys first, because deployment is its business: an app
/// lays out its own output, per reference or for everything at once. Only when it says nothing
/// does a class library's explicit choice apply, so that BinariesCopyPath on the ProjectReference
/// inside a wrapper library (often the only place the asm project is referenced) shapes the
/// app's output too, not just the library's own bin folder.
/// </para>
/// <para>
/// Errors: ASM0043 (a value that is not a preset or a usable relative folder, naming where it
/// was written). Warning: ASM0046 (two .NET libraries deploy one asm library differently; the
/// first reported wins).
/// </para>
/// </remarks>
public sealed class AsmResolveNativeLayouts : Task
{
    /// <summary>
    /// The asm libraries this project deploys, one item per report (an asm project may appear
    /// several times): the item is the asm project's full path, with metadata AsmImportName (its
    /// AssemblyName), BinariesCopyPath (as written on this project's own reference to it, raw),
    /// and for one a .NET library reported: ReportedExplicit ("true" when that library chose a
    /// layout itself), ReportedBinariesCopyPath (its choice, normalized; "" is AppFolder) and
    /// ReportedBy (that library, for messages).
    /// </summary>
    [Required]
    public ITaskItem[] Libraries { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project's BinariesCopyPath property, raw (empty when unset).</summary>
    public string ProjectBinariesCopyPath { get; set; } = "";

    /// <summary>The project's RuntimeIdentifier: empty for a portable build (decides what Auto means).</summary>
    public string RuntimeIdentifier { get; set; } = "";

    /// <summary>The project file, for messages.</summary>
    public string ProjectFile { get; set; } = "";

    /// <summary>
    /// One item per asm project: AsmImportName, BinariesCopyPath (normalized: Auto, "" for the app
    /// folder, or a folder pattern), Folder (resolved for this build: Auto made concrete),
    /// BinariesCopyPathSource (Reference, Project, Library or Default) and Explicit ("true" unless
    /// Default: what a referencing project may inherit).
    /// </summary>
    [Output]
    public ITaskItem[] Layouts { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Decides the layouts.</summary>
    /// <returns>True on success; false after logging ASM0043.</returns>
    public override bool Execute()
    {
        bool ridSpecific = RuntimeIdentifier.Length != 0;
        bool ok = true;

        // The project-wide value is checked once, even when no library uses it: a typo in a
        // property should not wait for the day a reference is added.
        if (!NativeLayout.TryNormalize(ProjectBinariesCopyPath, out string projectValue, out string? projectError))
        {
            Log.LogError(null, "ASM0043", null, ProjectFile, 0, 0, 0, 0,
                "BinariesCopyPath '{0}' cannot be used: {1}. Use Runtimes, AppFolder, RidFolders, Auto, or a folder such as native/{{rid}}.",
                ProjectBinariesCopyPath, projectError);
            ok = false;
        }

        bool projectSet = ProjectBinariesCopyPath.Trim().Length != 0;
        var layouts = new List<ITaskItem>();
        foreach (IGrouping<string, ITaskItem> library in Libraries.GroupBy(l => l.ItemSpec, StringComparer.OrdinalIgnoreCase))
        {
            string targetName = library.Select(l => l.GetMetadata("AsmImportName")).FirstOrDefault(n => n.Length != 0) ?? "";
            string value = NativeLayout.Auto;
            string source = "Default";

            // 1. This project's own reference to the library.
            ITaskItem? own = library.FirstOrDefault(l => l.GetMetadata("BinariesCopyPath").Trim().Length != 0);
            if (own is not null)
            {
                if (NativeLayout.TryNormalize(own.GetMetadata("BinariesCopyPath"), out string referenceValue, out string? error))
                {
                    value = referenceValue;
                    source = "Reference";
                }
                else
                {
                    Log.LogError(null, "ASM0043", null, ProjectFile, 0, 0, 0, 0,
                        "BinariesCopyPath '{0}' on the reference to {1} cannot be used: {2}. Use Runtimes, AppFolder, RidFolders, Auto, or a folder such as native/{{rid}}.",
                        own.GetMetadata("BinariesCopyPath"), System.IO.Path.GetFileName(library.Key), error);
                    ok = false;
                    continue;
                }
            }
            // 2. The project-wide property.
            else if (projectSet && projectError is null)
            {
                value = projectValue;
                source = "Project";
            }
            // 3. The explicit choice of a .NET library it comes through.
            else
            {
                // ReportedExplicit tells an explicit AppFolder (normalized to "") from no choice.
                var reported = library.Where(l => l.GetMetadata("ReportedExplicit") == "true").ToList();
                if (reported.Count != 0)
                {
                    value = reported[0].GetMetadata("ReportedBinariesCopyPath");
                    source = "Library";
                    ITaskItem? differing = reported.FirstOrDefault(r => r.GetMetadata("ReportedBinariesCopyPath") != reported[0].GetMetadata("ReportedBinariesCopyPath"));
                    if (differing is not null)
                    {
                        Log.LogWarning(null, "ASM0046", null, ProjectFile, 0, 0, 0, 0,
                            "{0} deploys {1} with BinariesCopyPath '{2}', {3} with '{4}': this project uses '{2}'. Set BinariesCopyPath on a reference to {1} here, or the property, to choose.",
                            reported[0].GetMetadata("ReportedBy"), System.IO.Path.GetFileName(library.Key), Show(reported[0].GetMetadata("ReportedBinariesCopyPath")),
                            differing.GetMetadata("ReportedBy"), Show(differing.GetMetadata("ReportedBinariesCopyPath")));
                    }
                }
            }

            var layout = new TaskItem(library.Key);
            layout.SetMetadata("AsmImportName", targetName);
            layout.SetMetadata("BinariesCopyPath", value);
            layout.SetMetadata("Folder", NativeLayout.Resolve(value, ridSpecific));
            layout.SetMetadata("BinariesCopyPathSource", source);
            layout.SetMetadata("Explicit", source == "Default" ? "false" : "true");
            layouts.Add(layout);
            Log.LogMessage(MessageImportance.Low, "{0}: BinariesCopyPath '{1}' ({2}), files under '{3}'.",
                System.IO.Path.GetFileName(library.Key), Show(value), source, Show(NativeLayout.Resolve(value, ridSpecific)));
        }

        Layouts = layouts.ToArray();
        return ok;
    }

    /// <summary>A value as messages show it: the app folder is written AppFolder, not as nothing.</summary>
    /// <param name="value">A normalized value or a folder.</param>
    /// <returns>The text to show.</returns>
    private static string Show(string value) => value.Length == 0 ? "AppFolder" : value;
}
