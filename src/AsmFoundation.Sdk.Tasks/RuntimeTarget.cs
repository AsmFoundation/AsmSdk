using System;
using System.Collections.Generic;
using System.Linq;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>The operating system family of a runtime identifier.</summary>
public enum TargetOs
{
    /// <summary>Windows: PE/COFF files, the Microsoft x64 or ARM64 calling convention.</summary>
    Windows,

    /// <summary>Linux: ELF files, System V (x64) or AAPCS64 (ARM64).</summary>
    Linux,

    /// <summary>macOS: Mach-O files; C symbols carry a leading underscore.</summary>
    MacOS,
}

/// <summary>The processor of a runtime identifier.</summary>
public enum TargetArchitecture
{
    /// <summary>x86-64. NASM assembles it (and llvm-mc, from GNU syntax).</summary>
    X64,

    /// <summary>AArch64. Only llvm-mc assembles it, from GNU syntax.</summary>
    Arm64,
}

/// <summary>
/// One runtime identifier AsmSdk builds for, decomposed into the facts the build needs, and the
/// file-name qualifiers that select sources for it.
/// </summary>
/// <remarks>
/// The set is fixed on purpose: each entry needs an object format, a linker driver, file names
/// and, for libc, a stub of that platform's C library. A RID outside the table is an error in
/// the SDK's targets (with the list), never a silent fallback to a neighbour.
/// </remarks>
public sealed class RuntimeTarget
{
    /// <summary>Every runtime identifier AsmSdk supports, by name.</summary>
    public static IReadOnlyDictionary<string, RuntimeTarget> All { get; } = new[]
    {
        new RuntimeTarget("win-x64", TargetOs.Windows, TargetArchitecture.X64, musl: false),
        new RuntimeTarget("win-arm64", TargetOs.Windows, TargetArchitecture.Arm64, musl: false),
        new RuntimeTarget("linux-x64", TargetOs.Linux, TargetArchitecture.X64, musl: false),
        new RuntimeTarget("linux-arm64", TargetOs.Linux, TargetArchitecture.Arm64, musl: false),
        new RuntimeTarget("linux-musl-x64", TargetOs.Linux, TargetArchitecture.X64, musl: true),
        new RuntimeTarget("osx-x64", TargetOs.MacOS, TargetArchitecture.X64, musl: false),
        new RuntimeTarget("osx-arm64", TargetOs.MacOS, TargetArchitecture.Arm64, musl: false),
    }.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The qualifiers a source file name may carry before its extension, besides the RIDs
    /// themselves: OS families, the Unix family and the CPUs ("crc32c.linux.asm").
    /// </summary>
    private static readonly HashSet<string> FamilyQualifiers =
        new(StringComparer.OrdinalIgnoreCase) { "win", "linux", "linux-musl", "osx", "unix", "x64", "arm64" };

    /// <summary>Creates an entry of the table.</summary>
    /// <param name="name">The runtime identifier, as .NET spells it.</param>
    /// <param name="os">Its operating system family.</param>
    /// <param name="architecture">Its processor.</param>
    /// <param name="musl">Whether its C library is musl rather than glibc (Linux only).</param>
    private RuntimeTarget(string name, TargetOs os, TargetArchitecture architecture, bool musl)
    {
        Name = name;
        Os = os;
        Architecture = architecture;
        IsMusl = musl;
    }

    /// <summary>The runtime identifier, as .NET spells it (linux-musl-x64).</summary>
    public string Name { get; }

    /// <summary>The operating system family.</summary>
    public TargetOs Os { get; }

    /// <summary>The processor.</summary>
    public TargetArchitecture Architecture { get; }

    /// <summary>Whether the C library is musl (Alpine) rather than glibc.</summary>
    public bool IsMusl { get; }

    /// <summary>
    /// Whether a word is a source qualifier at all (a RID, an OS family, unix, or a CPU), so that
    /// "my.data.asm" keeps "data" as part of its name.
    /// </summary>
    /// <param name="word">The last dot-separated part of a file name, before the extension.</param>
    /// <returns>True for a qualifier.</returns>
    public static bool IsQualifier(string word) => All.ContainsKey(word) || FamilyQualifiers.Contains(word);

    /// <summary>
    /// How specifically a qualifier names this target: higher wins when several files of one
    /// name apply. Zero means the qualifier does not apply at all.
    /// </summary>
    /// <param name="qualifier">The file's qualifier, or empty for an unqualified file.</param>
    /// <returns>
    /// 50 for this RID itself, 40 for linux-musl on a musl RID, 30 for the OS family (win, linux,
    /// osx), 20 for unix on Linux and macOS, 10 for the CPU (x64, arm64), 1 for an unqualified
    /// file, and 0 when the qualifier names another target.
    /// </returns>
    /// <remarks>
    /// An unqualified file scores 1, not 0, so that it applies everywhere its language does and
    /// still loses to any qualified file of the same name: crc32c.asm serves every x64 RID except
    /// those that have a crc32c.linux.asm or a crc32c.win-x64.asm.
    /// </remarks>
    public int Specificity(string qualifier)
    {
        if (qualifier.Length == 0)
        {
            return 1;
        }

        if (string.Equals(qualifier, Name, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return qualifier.ToLowerInvariant() switch
        {
            "linux-musl" => IsMusl ? 40 : 0,
            "win" => Os == TargetOs.Windows ? 30 : 0,
            "linux" => Os == TargetOs.Linux ? 30 : 0,
            "osx" => Os == TargetOs.MacOS ? 30 : 0,
            "unix" => Os != TargetOs.Windows ? 20 : 0,
            "x64" => Architecture == TargetArchitecture.X64 ? 10 : 0,
            "arm64" => Architecture == TargetArchitecture.Arm64 ? 10 : 0,
            _ => 0,
        };
    }
}
