using System;
using System.IO;
using AsmFoundation.Sdk.Bindings;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes a library's or program's C header, &lt;AssemblyName&gt;.h, from its API file (the owner's
/// choice of 2026-11-26: "Build it for 0.4.0", written by default, "allow to opt out":
/// AsmGenerateCHeader=false). The build writes one beside each API file (each RID's output folder,
/// and the merged one beside the RID folders); dotnet pack carries the merged one under include/.
/// </summary>
/// <remarks>
/// <para>
/// The text is <see cref="CHeaderEmitter.Write"/>'s: the functions' declarations, the structs and
/// their layout checks. Written only when it changes, so an unchanged header keeps its timestamp
/// (a C build that depends on it does not rebuild for nothing), and the same API file always
/// writes the same bytes (reproducible builds and packages).
/// </para>
/// <para>Errors: ASM0144 (the API file cannot be read), ASM0026 (the header cannot be written).</para>
/// </remarks>
public sealed class AsmWriteCHeader : Task
{
    /// <summary>The API file the header is written from.</summary>
    [Required]
    public string ApiFile { get; set; } = "";

    /// <summary>The header to write.</summary>
    [Required]
    public string HeaderFile { get; set; } = "";

    /// <summary>What writes it, for the header's comment ("AsmFoundation.Sdk 0.4.0").</summary>
    public string Origin { get; set; } = "AsmFoundation.Sdk";

    /// <summary>Reads the API file and writes the header.</summary>
    /// <returns>False after an error.</returns>
    public override bool Execute()
    {
        ApiLibraryModel library;
        try
        {
            library = ApiLibraryModel.Parse(File.ReadAllText(ApiFile), ApiFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.LogError(null, "ASM0144", null, ApiFile, 0, 0, 0, 0, "Cannot read the API file {0}: {1}", ApiFile, ex.Message);
            return false;
        }

        try
        {
            AsmReadApi.WriteIfChanged(HeaderFile, CHeaderEmitter.Write(library, Origin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "ASM0026", null, HeaderFile, 0, 0, 0, 0, "Cannot write the C header {0}: {1}", HeaderFile, ex.Message);
            return false;
        }

        return true;
    }
}
