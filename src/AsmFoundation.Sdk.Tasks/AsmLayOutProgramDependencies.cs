using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Decides where an asm program keeps what it needs at run time, for one RID: the shared
/// libraries it links and the programs it references (BinariesCopyPath), where each file goes
/// inside the program's folder, and the run paths the program must carry so that the OS loader
/// finds the libraries there.
/// </summary>
/// <remarks>
/// <para>
/// Which libraries: every link input that a referenced asm project or an asm package marked with
/// AsmRuntimeFile (the .so, .dylib or .dll to load; on Windows the program links the DLL's import
/// library, so the runtime file is another file than the link input), and, for packages packed
/// before that metadata existed, a .so or .dylib link input of a package, which is its own
/// runtime file. A static library has no run-time file and is never copied. A link input the
/// project names itself (an AsmLinkLibrary item of its own, such as a system library) is left
/// alone unless it carries AsmRuntimeFile: copying /usr/lib/libz.so next to a program would be
/// wrong.
/// </para>
/// <para>
/// Which programs: the files of each asm program the project references (a ProjectReference to
/// an .asmproj that builds a program, or a package that carries one), as that program's own
/// build lays them out: the program, the libraries it links and their PDBs, each at its path
/// relative to the program (AsmBundlePath). They move as one, so the referenced program's own run
/// paths keep finding its libraries wherever BinariesCopyPath puts it.
/// </para>
/// <para>
/// Where each goes, the same precedence as for a .NET app: BinariesCopyPath on the project's
/// own ProjectReference or PackageReference to it, then the project's BinariesCopyPath property,
/// then Auto, which for a program is the program's own folder (AppFolder). {rid} is the RID being
/// built. The other presets keep their meaning: Runtimes is runtimes/{rid}/native, RidFolders
/// is {rid}.
/// </para>
/// <para>
/// Run paths: a Linux program does not look for libraries in its own folder, and a macOS
/// program finds an AsmSdk dylib only through its run paths (the dylibs are linked with the
/// install name @rpath/&lt;file&gt;). Each folder that receives a library becomes one run path:
/// $ORIGIN/&lt;folder&gt; on Linux (DT_RUNPATH, which ld.lld writes by default), @loader_path/&lt;folder&gt;
/// on macOS (LC_RPATH). The program's DT_NEEDED entries name every linked library, transitive
/// ones included (an asm project gets its references' references, and a package's dependencies
/// bring their own props), so the program's run paths cover the libraries' own dependencies too.
/// A referenced program adds none: the loader never loads it into this one.
/// </para>
/// <para>
/// Windows has no run path: a program finds the DLLs it links in its own folder (the first place
/// the loader searches) or on PATH. A library folder other than the program's own stops the
/// build with ASM0047; delay-loading from a subfolder is a later step. A referenced program may go
/// anywhere: it is started, not loaded.
/// </para>
/// <para>
/// Two files on one path: byte for byte the same file (a library that both this program and a
/// program it references link) is copied once; anything else is ASM0044.
/// </para>
/// <para>
/// Errors: ASM0043 (a BinariesCopyPath value that is not a preset or a usable relative folder),
/// ASM0044 (two different files would land on one path), ASM0047 (Windows and a library folder
/// other than AppFolder). Warning: ASM0048 (an import library from an asm project or package
/// whose DLL the SDK cannot find, so the program would not start).
/// </para>
/// </remarks>
public sealed class AsmLayOutProgramDependencies : Task
{
    /// <summary>
    /// The program's link inputs (AsmLinkLibrary items), each with whatever metadata its source
    /// gave it: AsmRuntimeFile and AsmRuntimeSymbols (the file to load and its PDB), AsmOrigin
    /// (Project or Package), MSBuildSourceProjectFile (the asm project that returned it) and
    /// AsmPackage (the package id).
    /// </summary>
    [Required]
    public ITaskItem[] LinkLibraries { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The files of the programs this project references (AsmReferencedProgram items): each with
    /// AsmBundlePath (its path relative to its program, forward slashes), AsmProgramName (the
    /// program's file name, for messages), AsmSymbols=true on a PDB, and MSBuildSourceProjectFile
    /// (the asm project) or AsmPackage (the package id) for its BinariesCopyPath.
    /// </summary>
    public ITaskItem[] ReferencedPrograms { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project's asm ProjectReference items (FullPath, BinariesCopyPath as written).</summary>
    public ITaskItem[] ProjectReferences { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project's PackageReference items (the id, BinariesCopyPath as written).</summary>
    public ITaskItem[] PackageReferences { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The project's BinariesCopyPath property, raw (empty when unset).</summary>
    public string ProjectBinariesCopyPath { get; set; } = "";

    /// <summary>The RID being built: what {rid} stands for.</summary>
    [Required]
    public string RuntimeIdentifier { get; set; } = "";

    /// <summary>The OS of that RID: Windows, Linux or MacOS (the SDK's _AsmOs).</summary>
    [Required]
    public string TargetOs { get; set; } = "";

    /// <summary>The project file, for messages.</summary>
    public string ProjectFile { get; set; } = "";

    /// <summary>
    /// The files to copy next to (or below) the program: the item is the source file, TargetPath
    /// its place relative to the program's folder (forward slashes), AsmDependencyOrigin the
    /// project or package it came from (for messages), AsmDependencyKind Library or Program, and
    /// AsmSymbols=true on a PDB.
    /// </summary>
    [Output]
    public ITaskItem[] Files { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The linker arguments that give the program its run paths, quoted for the shell of this
    /// build machine (MSBuild's Exec runs cmd.exe on Windows and /bin/sh elsewhere); empty when
    /// the program links no shared library or builds for Windows.
    /// </summary>
    [Output]
    public string RunPathArguments { get; set; } = "";

    /// <summary>The help every ASM0043 of this task ends with.</summary>
    private const string ValueHelp = "Use AppFolder (the program's folder; Auto means it for a program), Runtimes, RidFolders, or a folder such as native/{rid}.";

    /// <summary>Lays the dependencies out.</summary>
    /// <returns>True on success; false after logging ASM0043, ASM0044 or ASM0047.</returns>
    public override bool Execute()
    {
        bool ok = true;

        // The project-wide value is checked even when nothing uses it, so that a typo shows the
        // day it is written, not the day a shared library is first linked.
        if (!NativeLayout.TryNormalize(ProjectBinariesCopyPath, out string projectValue, out string? projectError))
        {
            Log.LogError(null, "ASM0043", null, ProjectFile, 0, 0, 0, 0,
                "BinariesCopyPath '{0}' cannot be used: {1}. {2}", ProjectBinariesCopyPath, projectError, ValueHelp);
            ok = false;
        }

        var layout = new Layout(
            ProjectBinariesCopyPath.Trim().Length != 0 && projectError is null ? projectValue : NativeLayout.Auto,
            ReferenceValues(ProjectReferences, r => FullPath(r.GetMetadata("FullPath").Length != 0 ? r.GetMetadata("FullPath") : r.ItemSpec)),
            ReferenceValues(PackageReferences, r => r.ItemSpec));

        // Every source file once: the same library can reach the link line twice (a direct and a
        // transitive reference), and the first decides.
        var placed = new Placement();
        var runPathFolders = new List<string>();
        foreach (ITaskItem link in LinkLibraries)
        {
            string? runtimeFile = RuntimeFile(link);
            if (runtimeFile is null || !placed.FirstSight(FullPath(runtimeFile)))
            {
                continue;
            }

            string source = FullPath(runtimeFile);
            if (!TryFolder(link, layout, out string origin, out string folder))
            {
                ok = false;
                continue;
            }

            string folderForRid = NativeLayout.FolderFor(folder, RuntimeIdentifier);
            if (TargetOs == "Windows" && folderForRid.Length != 0)
            {
                Log.LogError(null, "ASM0047", null, ProjectFile, 0, 0, 0, 0,
                    "{0} would be copied to {1}/, but a Windows program finds the DLLs it links only in its own folder (or on PATH): Windows has no run path. Use BinariesCopyPath=\"AppFolder\" (or Auto) for {2} on {3}, for example with a Condition on the RuntimeIdentifier.",
                    Path.GetFileName(source), folderForRid, origin, RuntimeIdentifier);
                ok = false;
                continue;
            }

            ok &= Place(placed, source, NativeLayout.Destination(folder, RuntimeIdentifier, Path.GetFileName(source)), origin, "Library", symbols: false);
            if (!runPathFolders.Contains(folderForRid, StringComparer.Ordinal))
            {
                runPathFolders.Add(folderForRid);
            }

            // The PDB goes along when there is one, as .NET copies a referenced project's PDB: a
            // debugger then finds the library's symbols next to it.
            string symbols = link.GetMetadata("AsmRuntimeSymbols");
            if (symbols.Length != 0 && File.Exists(symbols) && placed.FirstSight(FullPath(symbols)))
            {
                ok &= Place(placed, FullPath(symbols), NativeLayout.Destination(folder, RuntimeIdentifier, Path.GetFileName(symbols)), origin, "Library", symbols: true);
            }
        }

        foreach (ITaskItem file in ReferencedPrograms)
        {
            string source = FullPath(file.ItemSpec);
            if (!placed.FirstSight(source))
            {
                continue;
            }

            if (!TryFolder(file, layout, out string origin, out string folder))
            {
                ok = false;
                continue;
            }

            // The bundle path keeps the program's own layout under the folder: its libraries stay
            // where its run paths look for them.
            string bundlePath = file.GetMetadata("AsmBundlePath");
            if (bundlePath.Length == 0)
            {
                bundlePath = Path.GetFileName(source);
            }

            string name = file.GetMetadata("AsmProgramName");
            ok &= Place(placed, source, NativeLayout.Destination(folder, RuntimeIdentifier, bundlePath),
                name.Length == 0 ? origin : $"the program {name} of {origin}", "Program", file.GetMetadata("AsmSymbols") == "true");
        }

        Files = placed.Files.ToArray();
        RunPathArguments = TargetOs == "Windows" ? "" : FormatRunPaths(runPathFolders);
        return ok;
    }

    /// <summary>The BinariesCopyPath values that apply to this project's dependencies.</summary>
    private sealed class Layout
    {
        /// <summary>Creates the layout.</summary>
        /// <param name="projectValue">The project's normalized value (Auto when it set none, or an unusable one).</param>
        /// <param name="references">Raw values on ProjectReference items, by the referenced project's full path.</param>
        /// <param name="packages">Raw values on PackageReference items, by package id.</param>
        public Layout(string projectValue, Dictionary<string, string> references, Dictionary<string, string> packages)
        {
            ProjectValue = projectValue;
            References = references;
            Packages = packages;
        }

        /// <summary>The project's normalized value (Auto when it set none, or an unusable one).</summary>
        public string ProjectValue { get; }

        /// <summary>Raw values on ProjectReference items, by the referenced project's full path.</summary>
        public Dictionary<string, string> References { get; }

        /// <summary>Raw values on PackageReference items, by package id.</summary>
        public Dictionary<string, string> Packages { get; }
    }

    /// <summary>The files placed so far, each source and each destination once.</summary>
    private sealed class Placement
    {
        /// <summary>The source files already seen (full paths).</summary>
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The files to copy, in the order placed.</summary>
        public List<ITaskItem> Files { get; } = new();

        /// <summary>Each destination's source file.</summary>
        public Dictionary<string, string> ClaimedBy { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records a source file, and says whether this is its first appearance.</summary>
        /// <param name="source">The file's full path.</param>
        /// <returns>True the first time; false for a file already placed (or refused).</returns>
        public bool FirstSight(string source) => _seen.Add(source);
    }

    /// <summary>
    /// The BinariesCopyPath folder of one dependency: its reference's value, else the project's,
    /// else Auto (the program's own folder).
    /// </summary>
    /// <param name="dependency">A link input or a program file (MSBuildSourceProjectFile or AsmPackage tells its reference).</param>
    /// <param name="layout">The values in effect.</param>
    /// <param name="origin">Its project file name or "the package &lt;id&gt;", for messages.</param>
    /// <param name="folder">The resolved folder pattern ("" for the program's folder; may contain {rid}).</param>
    /// <returns>False after logging ASM0043 for an unusable value on its reference.</returns>
    private bool TryFolder(ITaskItem dependency, Layout layout, out string origin, out string folder)
    {
        string? raw;
        string package = dependency.GetMetadata("AsmPackage");
        if (package.Length != 0)
        {
            origin = "the package " + package;
            layout.Packages.TryGetValue(package, out raw);
        }
        else
        {
            string project = FullPath(dependency.GetMetadata("MSBuildSourceProjectFile"));
            origin = project.Length != 0 ? Path.GetFileName(project) : Path.GetFileName(dependency.ItemSpec);
            layout.References.TryGetValue(project, out raw);
        }

        string value = layout.ProjectValue;
        if (raw is not null && raw.Trim().Length != 0 && !NativeLayout.TryNormalize(raw, out value, out string? error))
        {
            Log.LogError(null, "ASM0043", null, ProjectFile, 0, 0, 0, 0,
                "BinariesCopyPath '{0}' on the reference to {1} cannot be used: {2}. {3}", raw, origin, error, ValueHelp);
            folder = "";
            return false;
        }

        // A program is built for one RID at a time: Auto is its own folder, as it is for a
        // RID-specific .NET app.
        folder = NativeLayout.Resolve(value, ridSpecific: true);
        return true;
    }

    /// <summary>
    /// The file a link input loads at run time, or null when it has none the program needs.
    /// </summary>
    /// <param name="link">A link input.</param>
    /// <returns>The runtime file's path, or null.</returns>
    /// <remarks>
    /// AsmRuntimeFile when present (projects and packages of this SDK set it on every shared link
    /// input). Without it, a .so or .dylib from a package or a project is its own runtime file
    /// (packages packed by AsmSdk 0.3.0); a Windows import library from either has a DLL that
    /// this SDK cannot name, which ASM0048 reports. Anything else (static libraries, objects, the
    /// project's own link inputs) needs no copy.
    /// </remarks>
    private string? RuntimeFile(ITaskItem link)
    {
        string runtime = link.GetMetadata("AsmRuntimeFile");
        if (runtime.Length != 0)
        {
            return runtime;
        }

        string origin = link.GetMetadata("AsmOrigin");
        if (origin != "Project" && origin != "Package")
        {
            return null;
        }

        string name = Path.GetFileName(link.ItemSpec);
        if (name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
        {
            return link.ItemSpec;
        }

        if (name.EndsWith(".dll.lib", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogWarning(null, "ASM0048", null, ProjectFile, 0, 0, 0, 0,
                "{0} links {1}, the import library of {2}, but {3} does not say where {2} is, so it is not copied next to the program and the program will not start without it. Rebuild or repack {3} with this AsmSdk, or copy {2} next to the program yourself.",
                Path.GetFileName(ProjectFile), name, name.Substring(0, name.Length - ".lib".Length),
                link.GetMetadata("AsmPackage").Length != 0 ? "the package " + link.GetMetadata("AsmPackage") : "its project");
        }

        return null;
    }

    /// <summary>
    /// Records one file's destination. Another file on the same path is fine when it has the same
    /// bytes (one copy serves both); otherwise ASM0044.
    /// </summary>
    /// <param name="placed">The files placed so far (appended to).</param>
    /// <param name="source">The file's full path.</param>
    /// <param name="destination">Its path relative to the program's folder, forward slashes.</param>
    /// <param name="origin">Its project, package or program, for messages.</param>
    /// <param name="kind">Library or Program.</param>
    /// <param name="symbols">Whether it is a PDB.</param>
    /// <returns>False after logging ASM0044.</returns>
    private bool Place(Placement placed, string source, string destination, string origin, string kind, bool symbols)
    {
        if (placed.ClaimedBy.TryGetValue(destination, out string? other))
        {
            if (SameBytes(other, source))
            {
                return true;
            }

            Log.LogError(null, "ASM0044", null, ProjectFile, 0, 0, 0, 0,
                "{0} and {1} ({2}) would both be copied to {3} next to the program. Give the libraries different AssemblyNames, or the references different BinariesCopyPath folders.",
                other, source, origin, destination);
            return false;
        }

        placed.ClaimedBy[destination] = source;
        var item = new TaskItem(source);
        item.SetMetadata("TargetPath", destination);
        item.SetMetadata("AsmDependencyOrigin", origin);
        item.SetMetadata("AsmDependencyKind", kind);
        if (symbols)
        {
            item.SetMetadata("AsmSymbols", "true");
        }

        placed.Files.Add(item);
        return true;
    }

    /// <summary>Whether two files hold the same bytes (two builds of one library are, with lld's content-derived ids).</summary>
    /// <param name="first">A file.</param>
    /// <param name="second">Another file.</param>
    /// <returns>True when both can be read and are equal; false otherwise, unreadable included.</returns>
    private static bool SameBytes(string first, string second)
    {
        try
        {
            var a = new FileInfo(first);
            var b = new FileInfo(second);
            if (!a.Exists || !b.Exists || a.Length != b.Length)
            {
                return false;
            }

            return File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: treat as different, so the collision is reported, not hidden.
            return false;
        }
    }

    /// <summary>
    /// The linker's run-path arguments for these folders, in the order the libraries came, quoted
    /// for this build machine's shell.
    /// </summary>
    /// <param name="folders">Folders relative to the program ("" for its own).</param>
    /// <returns>For example <c>-rpath '$ORIGIN' -rpath '$ORIGIN/native/linux-x64'</c>; empty for no folder.</returns>
    /// <remarks>
    /// The quoting matters because MSBuild's Exec hands the line to a shell: /bin/sh would expand
    /// $ORIGIN to nothing inside double quotes, so Unix build machines get single quotes (an
    /// apostrophe in a folder name closes, escapes and reopens them); cmd.exe leaves $ORIGIN
    /// alone but expands %NAME%, so Windows build machines get double quotes with % doubled
    /// (NativeLayout already refuses " in a folder).
    /// </remarks>
    private string FormatRunPaths(List<string> folders)
    {
        string anchor = TargetOs == "MacOS" ? "@loader_path" : "$ORIGIN";
        bool windowsShell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var arguments = new StringBuilder();
        foreach (string folder in folders)
        {
            string path = folder.Length == 0 ? anchor : anchor + "/" + folder;
            string quoted = windowsShell
                ? "\"" + path.Replace("%", "%%") + "\""
                : "'" + path.Replace("'", "'\\''") + "'";
            if (arguments.Length != 0)
            {
                arguments.Append(' ');
            }

            arguments.Append("-rpath ").Append(quoted);
        }

        return arguments.ToString();
    }

    /// <summary>The BinariesCopyPath written on each reference, by key (the first item of a key wins).</summary>
    /// <param name="references">ProjectReference or PackageReference items.</param>
    /// <param name="key">The key of an item: a project's full path, or a package id.</param>
    /// <returns>The raw values, case-insensitive keys; references without the metadata are left out.</returns>
    private static Dictionary<string, string> ReferenceValues(ITaskItem[] references, Func<ITaskItem, string> key)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem reference in references)
        {
            string value = reference.GetMetadata("BinariesCopyPath");
            string name = key(reference);
            if (value.Trim().Length != 0 && name.Length != 0 && !values.ContainsKey(name))
            {
                values[name] = value;
            }
        }

        return values;
    }

    /// <summary>A path in the form used as a key: full, with the OS's separators.</summary>
    /// <param name="path">A path as an item names it.</param>
    /// <returns>The full path; the input itself when it is empty or not a valid path.</returns>
    private static string FullPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
