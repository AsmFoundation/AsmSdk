using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Writes the link-time stand-in for a platform's C library (or system DLL) from the SDK's
/// symbol lists: the file the linker reads in place of the real library, which a build machine
/// of another OS does not have.
/// </summary>
/// <remarks>
/// <para>Formats (<see cref="Format"/>):</para>
/// <list type="bullet">
/// <item><c>Nasm</c> / <c>Gnu</c>: the source of an ELF shared library (x64 NASM, ARM64 GNU
/// syntax for llvm-mc) defining every function as a bare return and every data object as
/// zeros of its size; the SDK assembles and links it under the C library's SONAME.</item>
/// <item><c>Tbd</c>: a text-based stub (TAPI v4, the format of Apple's SDK stubs) that ld64.lld
/// links against directly, with Mach-O's underscore added to each C name.</item>
/// <item><c>Def</c>: a module-definition file, from which lld-link's library mode writes the
/// import library of a Windows DLL.</item>
/// </list>
/// <para>
/// The symbol files (stubs/*.symbols) hold one name per word, "#" comments, and the lines
/// "data &lt;name&gt; &lt;size&gt;" (a data object; ELF copy relocations copy exactly that many
/// bytes) and "raw &lt;name&gt;" (a Mach-O name used as written). Names are written once each, in
/// file order; <see cref="ExtraSymbols"/> (the project's AsmLibcSymbol items) are added as
/// functions. The output is written only when its text changes, so the stub is not rebuilt on
/// every build. Error: ASM0070 (a symbol file cannot be read or a line is malformed, or the
/// output cannot be written).
/// </para>
/// </remarks>
public sealed class AsmWriteCLibraryStub : Task
{
    /// <summary>Nasm, Gnu, Tbd or Def.</summary>
    [Required]
    public string Format { get; set; } = "";

    /// <summary>The symbol lists, read in order.</summary>
    [Required]
    public ITaskItem[] SymbolFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>More function names (the project's AsmLibcSymbol items).</summary>
    public ITaskItem[] ExtraSymbols { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Tbd: the install name (/usr/lib/libSystem.B.dylib). Def: the DLL's file name (ucrtbase.dll).</summary>
    public string LibraryName { get; set; } = "";

    /// <summary>Tbd: the TAPI target the stub serves (x86_64-macos or arm64-macos).</summary>
    public string Target { get; set; } = "";

    /// <summary>The file to write.</summary>
    [Required]
    public string OutputPath { get; set; } = "";

    /// <summary>Writes the stub.</summary>
    /// <returns>True on success; false after logging ASM0070.</returns>
    public override bool Execute()
    {
        var functions = new List<string>();
        var data = new List<(string Name, int Size)>();
        var raw = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (ITaskItem file in SymbolFiles)
            {
                Read(file.GetMetadata("FullPath"), functions, data, raw, seen);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.LogError(null, "ASM0070", null, OutputPath, 0, 0, 0, 0, "Cannot read the C library symbol lists: {0}", ex.Message);
            return false;
        }

        foreach (ITaskItem extra in ExtraSymbols)
        {
            if (seen.Add(extra.ItemSpec))
            {
                functions.Add(extra.ItemSpec);
            }
        }

        string text;
        switch (Format)
        {
            case "Nasm": text = WriteNasm(functions, data); break;
            case "Gnu": text = WriteGnu(functions, data); break;
            case "Tbd": text = WriteTbd(functions, data, raw); break;
            case "Def": text = WriteDef(functions); break;
            default:
                Log.LogError(null, "ASM0070", null, OutputPath, 0, 0, 0, 0, "Unknown stub format '{0}' (Nasm, Gnu, Tbd or Def).", Format);
                return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
            if (!File.Exists(OutputPath) || File.ReadAllText(OutputPath) != text)
            {
                File.WriteAllText(OutputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "ASM0070", null, OutputPath, 0, 0, 0, 0, "Cannot write {0}: {1}", OutputPath, ex.Message);
            return false;
        }

        return true;
    }

    /// <summary>Reads one symbol list.</summary>
    /// <param name="path">The file.</param>
    /// <param name="functions">Function names, appended in order.</param>
    /// <param name="data">Data objects, appended in order.</param>
    /// <param name="raw">Names used as written, appended in order.</param>
    /// <param name="seen">Every name so far, so that each is written once.</param>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    /// <exception cref="FormatException">A data line lacks its size, or a raw line its name.</exception>
    private static void Read(string path, List<string> functions, List<(string, int)> data, List<string> raw, HashSet<string> seen)
    {
        int number = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            number++;
            int hash = line.IndexOf('#');
            string[] words = (hash < 0 ? line : line.Substring(0, hash)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            if (words[0] == "data")
            {
                if (words.Length != 3 || !int.TryParse(words[2], out int size) || size <= 0)
                {
                    throw new FormatException($"{path}({number}): expected \"data <name> <size>\"");
                }

                if (seen.Add(words[1]))
                {
                    data.Add((words[1], size));
                }

                continue;
            }

            if (words[0] == "raw")
            {
                if (words.Length != 2)
                {
                    throw new FormatException($"{path}({number}): expected \"raw <name>\"");
                }

                if (seen.Add(words[1]))
                {
                    raw.Add(words[1]);
                }

                continue;
            }

            foreach (string name in words)
            {
                if (seen.Add(name))
                {
                    functions.Add(name);
                }
            }
        }
    }

    /// <summary>The comment every generated file starts with.</summary>
    /// <param name="prefix">The format's comment marker.</param>
    /// <param name="functions">How many functions.</param>
    /// <param name="data">How many data objects.</param>
    /// <returns>The comment lines.</returns>
    private string Header(string prefix, int functions, int data) =>
        $"{prefix} Generated by AsmFoundation.Sdk (AsmWriteCLibraryStub) from {string.Join(", ", SymbolFiles.Select(f => Path.GetFileName(f.ItemSpec)))}: " +
        $"{functions} functions, {data} data objects. A link-time stand-in: nothing in it ever runs.\n";

    /// <summary>The x64 ELF stub in NASM syntax.</summary>
    /// <param name="functions">Function names.</param>
    /// <param name="data">Data objects.</param>
    /// <returns>The source.</returns>
    /// <remarks>The $ prefix makes NASM read each name as a symbol even when it is an instruction mnemonic (libc has wait, the x87 WAIT, and syscall).</remarks>
    private string WriteNasm(List<string> functions, List<(string Name, int Size)> data)
    {
        var text = new StringBuilder(Header(";", functions.Count, data.Count));
        text.Append("bits 64\nsection .text\n");
        foreach (string name in functions)
        {
            text.Append("global $").Append(name).Append(":function\n$").Append(name).Append(":\n    ret\n");
        }

        text.Append("section .data\nalign 8\n");
        foreach ((string name, int size) in data)
        {
            text.Append("global $").Append(name).Append(":data ").Append(size).Append("\n$").Append(name).Append(":\n    times ").Append(size).Append(" db 0\n");
        }

        text.Append("section .note.GNU-stack noalloc noexec nowrite progbits\n");
        return text.ToString();
    }

    /// <summary>The ARM64 (or any CPU llvm-mc knows) ELF stub in GNU syntax.</summary>
    /// <param name="functions">Function names.</param>
    /// <param name="data">Data objects.</param>
    /// <returns>The source.</returns>
    private string WriteGnu(List<string> functions, List<(string Name, int Size)> data)
    {
        var text = new StringBuilder(Header("//", functions.Count, data.Count));
        text.Append("    .text\n");
        foreach (string name in functions)
        {
            text.Append("    .globl ").Append(name).Append("\n    .type ").Append(name).Append(", %function\n").Append(name).Append(":\n    ret\n");
        }

        text.Append("    .data\n    .p2align 3\n");
        foreach ((string name, int size) in data)
        {
            text.Append("    .globl ").Append(name).Append("\n    .type ").Append(name).Append(", %object\n    .size ").Append(name).Append(", ").Append(size)
                .Append('\n').Append(name).Append(":\n    .zero ").Append(size).Append('\n');
        }

        text.Append("    .section .note.GNU-stack,\"\",%progbits\n");
        return text.ToString();
    }

    /// <summary>The macOS text-based stub (TAPI v4).</summary>
    /// <param name="functions">C function names (an underscore is added).</param>
    /// <param name="data">C data names (an underscore is added; sizes do not exist in a .tbd).</param>
    /// <param name="raw">Mach-O names used as written.</param>
    /// <returns>The YAML document.</returns>
    /// <remarks>
    /// Compatibility version 1, as libSystem.B.dylib's: dyld refuses to load a program whose
    /// LC_LOAD_DYLIB asks for a higher one than the library has. Names are single-quoted, so
    /// "$" in "_stat$INODE64" and the like stays literal.
    /// </remarks>
    private string WriteTbd(List<string> functions, List<(string Name, int Size)> data, List<string> raw)
    {
        IEnumerable<string> symbols = functions.Select(f => "_" + f).Concat(data.Select(d => "_" + d.Name)).Concat(raw).OrderBy(s => s, StringComparer.Ordinal);
        // The tag line comes first: LLVM recognizes a text stub by the file starting with
        // "--- !tapi" (llvm::identify_magic), so the comment goes inside the document.
        var text = new StringBuilder("--- !tapi-tbd\n");
        text.Append(Header("#", functions.Count, data.Count));
        text.Append("tbd-version:     4\n");
        text.Append("targets:         [ ").Append(Target).Append(" ]\n");
        text.Append("install-name:    '").Append(LibraryName).Append("'\n");
        text.Append("current-version: 1351\n");
        text.Append("compatibility-version: 1\n");
        text.Append("exports:\n");
        text.Append("  - targets:         [ ").Append(Target).Append(" ]\n");
        text.Append("    symbols:         [ ").Append(string.Join(",\n                       ", symbols.Select(s => "'" + s.Replace("'", "''") + "'"))).Append(" ]\n");
        text.Append("...\n");
        return text.ToString();
    }

    /// <summary>The module-definition file of a Windows DLL.</summary>
    /// <param name="functions">Exported names.</param>
    /// <returns>The .def text.</returns>
    private string WriteDef(List<string> functions)
    {
        var text = new StringBuilder(Header(";", functions.Count, 0));
        text.Append("LIBRARY \"").Append(LibraryName).Append("\"\nEXPORTS\n");
        foreach (string name in functions)
        {
            text.Append("    ").Append(name).Append('\n');
        }

        return text.ToString();
    }
}
