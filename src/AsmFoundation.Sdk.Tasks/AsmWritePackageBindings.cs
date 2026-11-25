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
/// Writes the C# bindings' baseline an asm package carries for its .NET consumers (phase 4 of the
/// bindings, the owner's spec of 2026-11-25: "at pack time into
/// contentFiles/cs/net7.0/AsmBindings/&lt;Library&gt;.cs.pp for packages, with tokens from the app's
/// properties"): every layout at once, from the library's merged API file, through the same
/// emitter as the build-time baseline (<see cref="CSharpEmitter.WritePackageBaseline"/>).
/// </summary>
/// <remarks>
/// <para>
/// The tokens are named for the package (<see cref="TokenPrefix"/>), so two packages' baselines
/// never share a value; the package's targets (AsmWritePackageConsumerTargets) fill them from the
/// consumer's settings and check those first. The source is written only when its text changes, so
/// an unchanged package stays byte-identical (reproducible packs).
/// </para>
/// <para>
/// Collisions: a layout in which two names meet cannot be compiled. Packing goes on (the default
/// layout may be fine, and a package is also for C and asm consumers), with warning ASM0148 per
/// collision; the baseline keeps ASM0143 as an #error in that layout, so a consumer choosing it is
/// told the same as a ProjectReference consumer.
/// </para>
/// <para>Errors: ASM0144 (the API file cannot be read), ASM0061 (the source cannot be written).</para>
/// </remarks>
public sealed class AsmWritePackageBindings : Task
{
    /// <summary>The library's API file, merged over its RIDs: the one of the target framework a .NET consumer gets (libc's when the project has it).</summary>
    [Required]
    public string ApiFile { get; set; } = "";

    /// <summary>The package id: the tokens' prefix is made from it.</summary>
    [Required]
    public string PackageId { get; set; } = "";

    /// <summary>The package version, for the header.</summary>
    public string PackageVersion { get; set; } = "";

    /// <summary>What writes it, for the header ("AsmFoundation.Sdk 0.3.0").</summary>
    public string Origin { get; set; } = "AsmFoundation.Sdk";

    /// <summary>The .cs.pp to write.</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>The library's import name (its AssemblyName), from the API file.</summary>
    [Output]
    public string Library { get; set; } = "";

    /// <summary>The baseline's namespace when the consumer names none: the library's name in PascalCase.</summary>
    [Output]
    public string DefaultNamespace { get; set; } = "";

    /// <summary>The tokens' prefix ("asmbindings_Test_Calc_"), for the targets that fill them.</summary>
    [Output]
    public string TokenPrefix { get; set; } = "";

    /// <summary>Reads the API file and writes the baseline.</summary>
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

        TokenPrefix = TokenPrefixOf(PackageId);
        Library = library.Library;
        DefaultNamespace = CSharpEmitter.LibraryNamespace(library.Library);

        var body = new StringBuilder();
        var layoutCollisions = new Dictionary<Nesting, List<string>>();
        var structCollisions = new List<string>();
        CSharpEmitter.WritePackageBaseline(body, library, TokenPrefix, layoutCollisions, structCollisions);

        // Reported with the default namespace in place of its token: the packer reads them, and
        // a consumer's own namespace is not known here.
        string token = "$" + TokenPrefix + "namespace$";
        foreach (string collision in structCollisions)
        {
            Log.LogWarning(null, "ASM0148", null, ApiFile, 0, 0, 0, 0,
                "The bindings of {0} in the package {1} hold {2}: no consumer of the package can compile them (ASM0143). Rename one of the fields.",
                library.Library, PackageId, collision.Replace(token, DefaultNamespace));
        }

        foreach (KeyValuePair<Nesting, List<string>> layout in layoutCollisions.OrderBy(l => (int)l.Key))
        {
            foreach (string collision in layout.Value)
            {
                Log.LogWarning(null, "ASM0148", null, ApiFile, 0, 0, 0, 0,
                    "The bindings of {0} in the package {1}, laid out as {2}: {3}. A consumer that chooses this layout gets ASM0143; rename one of them.",
                    library.Library, PackageId, layout.Key, collision.Replace(token, DefaultNamespace));
            }
        }

        // The header says what fills the tokens; never the word the consumers' switch searches
        // their sources for (the baseline would turn the generator on in each of them).
        string header =
            "// <auto-generated/> by " + Origin + " for the package " + PackageId + (PackageVersion.Length == 0 ? "" : " " + PackageVersion) +
            ", from " + Path.GetFileName(ApiFile) + ": the C# bindings' baseline of the asm library " + library.Library + ".\n" +
            "// NuGet fills each $" + TokenPrefix + "...$ token from the project's AsmBindings settings (the package's targets\n" +
            "// compute and check them), and the #define lines below choose the layout.\n";
        try
        {
            AsmReadApi.WriteIfChanged(OutputPath, header + body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "ASM0061", null, OutputPath, 0, 0, 0, 0, "Cannot write the package's bindings {0}: {1}", OutputPath, ex.Message);
            return false;
        }

        return !Log.HasLoggedErrors;
    }

    /// <summary>
    /// The prefix of a package's tokens: "asmbindings_", the package id with every character other
    /// than a letter or digit made "_" (<see cref="AsmWritePackageConsumerTargets.Sanitize"/>), then
    /// "_". Footgun: two package ids that differ only in punctuation (A.B and A_B) share a prefix,
    /// so a consumer of both would fill both baselines from one package's values; the package's
    /// targets are named with the same suffix and meet the same way, a limit accepted since the
    /// package targets were first written.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>For example "asmbindings_Test_Calc_" for Test.Calc.</returns>
    internal static string TokenPrefixOf(string packageId) => "asmbindings_" + AsmWritePackageConsumerTargets.Sanitize(packageId) + "_";
}
