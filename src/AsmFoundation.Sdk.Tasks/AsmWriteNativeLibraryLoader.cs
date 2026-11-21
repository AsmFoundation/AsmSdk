using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes the generated fallback loader of a C# project (see <see cref="NativeLoaderSource"/>):
/// one class that knows where this project's asm libraries are deployed.
/// </summary>
/// <remarks>
/// The file is written only when its text changes, so an unchanged layout leaves CoreCompile
/// up to date. Libraries deployed to the app folder are left out (.NET finds them there by
/// itself); with none left, nothing is written and <see cref="Written"/> is false, so the caller
/// adds no Compile item. Error: ASM0045 (the file cannot be written).
/// </remarks>
public sealed class AsmWriteNativeLibraryLoader : Task
{
    /// <summary>The layouts (item = asm project path, metadata AsmImportName, Folder and AsmKind; programs are skipped).</summary>
    [Required]
    public ITaskItem[] Layouts { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Where to write the source (under the intermediate folder).</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>The class name; unique in the assembly.</summary>
    public string ClassName { get; set; } = "AsmNativeLibraryLoader";

    /// <summary>Who writes it, for the file's header (for example "AsmFoundation.Sdk 0.3.0 for Calc.App").</summary>
    public string Origin { get; set; } = "AsmFoundation.Sdk";

    /// <summary>True when there is a loader to compile (the file exists and is current).</summary>
    [Output]
    public bool Written { get; set; }

    /// <summary>Writes the loader.</summary>
    /// <returns>True on success; false after logging ASM0045.</returns>
    public override bool Execute()
    {
        // One entry per library name: two asm projects with one AssemblyName collide in the
        // output anyway (ASM0044), so the first folder is the one that matters.
        var libraries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ITaskItem layout in Layouts)
        {
            // A program is started, never loaded through [DllImport]: it has no place in a
            // loader's table (and a library of the same name would lose its entry to it).
            if (layout.GetMetadata("AsmKind") == "Program")
            {
                continue;
            }

            string name = layout.GetMetadata("AsmImportName");
            string folder = layout.GetMetadata("Folder");
            if (name.Length != 0 && folder.Length != 0 && !libraries.ContainsKey(name))
            {
                libraries[name] = folder;
            }
        }

        if (libraries.Count == 0)
        {
            Written = false;
            return true;
        }

        try
        {
            // Inside the try: Generate refuses a name it cannot put in a C# string literal
            // (ArgumentException), which must end as ASM0045, not as an unhandled task failure.
            string text = NativeLoaderSource.Generate(ClassName, libraries, Origin);
            string? existing = File.Exists(OutputPath) ? File.ReadAllText(OutputPath) : null;
            if (existing != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
                File.WriteAllText(OutputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.LogError(null, "ASM0045", null, OutputPath, 0, 0, 0, 0,
                "Cannot write the native library loader {0}: {1}", OutputPath, ex.Message);
            return false;
        }

        Written = true;
        Log.LogMessage(MessageImportance.Low, "Native library loader for {0}: {1}", string.Join(", ", libraries.Keys.OrderBy(k => k, StringComparer.Ordinal)), OutputPath);
        return true;
    }
}
