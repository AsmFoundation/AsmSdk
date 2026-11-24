using System.Text;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>What a WebAssembly binary imports or exports: a function, table, memory, global or tag.</summary>
/// <param name="Module">The import's module ("env", "wasi_snapshot_preview1"); empty for an export.</param>
/// <param name="Name">The import's or export's name.</param>
/// <param name="Kind">function, table, memory, global or tag (the binary's kind byte, named as JavaScript's WebAssembly.Module.exports names it).</param>
public sealed record WasmEntry(string Module, string Name, string Kind);

/// <summary>
/// Reads the sections of a WebAssembly binary, a linked module or a relocatable object, far enough
/// to tell what it imports, exports and carries: the tests check WebAssembly outputs without Node
/// or any tool of their own, on every CI machine.
/// </summary>
/// <remarks>
/// <para>
/// The format (the WebAssembly core specification, binary format): the magic "\0asm", version 1,
/// then sections, each an id byte and a LEB128 size. Section 2 lists imports (module, name, kind,
/// then a description that depends on the kind), section 7 exports (name, kind, index), section 0
/// a custom section (a name, then anything: "name", "linking", ".debug_line", ...). Everything else
/// is skipped by its size.
/// </para>
/// <para>
/// Only what the tests need is decoded: the import descriptions are skipped by their shape, which
/// is why every kind is handled (a table or global import has a different length than a function
/// import, and a wrong length would misread every entry after it).
/// </para>
/// </remarks>
public sealed class WasmBinary
{
    /// <summary>Decodes a binary.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <exception cref="InvalidDataException">The bytes are not a version 1 WebAssembly binary, or a section runs past the end.</exception>
    private WasmBinary(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0 || bytes[1] != (byte)'a' || bytes[2] != (byte)'s' || bytes[3] != (byte)'m' || BitConverter.ToUInt32(bytes, 4) != 1)
        {
            throw new InvalidDataException("not a WebAssembly binary (magic \\0asm, version 1)");
        }

        var imports = new List<WasmEntry>();
        var exports = new List<WasmEntry>();
        var custom = new List<string>();
        var payloads = new Dictionary<string, List<byte>>(StringComparer.Ordinal);
        var ids = new List<int>();
        int position = 8;
        while (position < bytes.Length)
        {
            int id = bytes[position++];
            int size = (int)ReadUnsigned(bytes, ref position);
            int end = position + size;
            if (end > bytes.Length)
            {
                throw new InvalidDataException($"section {id} runs past the end of the file");
            }

            ids.Add(id);
            int cursor = position;
            switch (id)
            {
                case 0:
                {
                    string name = ReadName(bytes, ref cursor);
                    custom.Add(name);
                    // Several sections of one name read as one payload, in order, as the SDK
                    // reads its records (ObjectSections.ReadWasm).
                    if (!payloads.TryGetValue(name, out List<byte>? payload))
                    {
                        payloads[name] = payload = new List<byte>();
                    }

                    payload.AddRange(new ArraySegment<byte>(bytes, cursor, end - cursor));
                    break;
                }

                case 2:
                    for (uint count = ReadUnsigned(bytes, ref cursor), i = 0; i < count; i++)
                    {
                        string module = ReadName(bytes, ref cursor);
                        string name = ReadName(bytes, ref cursor);
                        byte kind = bytes[cursor++];
                        SkipImportDescription(bytes, ref cursor, kind);
                        imports.Add(new WasmEntry(module, name, KindName(kind)));
                    }

                    break;
                case 7:
                    for (uint count = ReadUnsigned(bytes, ref cursor), i = 0; i < count; i++)
                    {
                        string name = ReadName(bytes, ref cursor);
                        byte kind = bytes[cursor++];
                        ReadUnsigned(bytes, ref cursor);
                        exports.Add(new WasmEntry("", name, KindName(kind)));
                    }

                    break;
            }

            position = end;
        }

        Imports = imports;
        Exports = exports;
        CustomSections = custom;
        _customPayloads = payloads.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        SectionIds = ids;
    }

    /// <summary>Everything the binary imports, in order.</summary>
    public IReadOnlyList<WasmEntry> Imports { get; }

    /// <summary>Everything the binary exports, in order (a relocatable object exports nothing).</summary>
    public IReadOnlyList<WasmEntry> Exports { get; }

    /// <summary>Each custom section's payload (after its name), by name.</summary>
    private readonly Dictionary<string, byte[]> _customPayloads;

    /// <summary>The names of the custom sections ("name", "linking", ".debug_info", ...).</summary>
    public IReadOnlyList<string> CustomSections { get; }

    /// <summary>
    /// The NUL-terminated text records of a custom section, in order: what asmsdk-wasm.inc writes
    /// into "asmapi" ("proto ...", "fn ..."), as JavaScript's WebAssembly.Module.customSections
    /// would hand them over.
    /// </summary>
    /// <param name="name">The custom section's name.</param>
    /// <returns>The records; empty when the binary has no such section.</returns>
    public IReadOnlyList<string> CustomRecords(string name) =>
        _customPayloads.TryGetValue(name, out byte[]? payload)
            ? System.Text.Encoding.UTF8.GetString(payload).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

    /// <summary>The id of every section, in order (1 type, 2 import, 3 function, 7 export, 10 code, 11 data, ...).</summary>
    public IReadOnlyList<int> SectionIds { get; }

    /// <summary>The names of the exported functions, in order.</summary>
    public IEnumerable<string> ExportedFunctions => Exports.Where(e => e.Kind == "function").Select(e => e.Name);

    /// <summary>Reads a file as a WebAssembly binary.</summary>
    /// <param name="path">A .wasm module or a wasm object (.o).</param>
    /// <returns>Its decoded sections.</returns>
    /// <exception cref="InvalidDataException">The file is not a WebAssembly binary.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static WasmBinary Read(string path) => new(File.ReadAllBytes(path));

    /// <summary>Reads an unsigned LEB128 number (at most 32 bits, as the format's counts and sizes are).</summary>
    /// <param name="bytes">The binary.</param>
    /// <param name="position">Where the number starts; moved past it.</param>
    /// <returns>The number.</returns>
    /// <exception cref="InvalidDataException">The number runs past the end or past 32 bits.</exception>
    private static uint ReadUnsigned(byte[] bytes, ref int position)
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            if (position >= bytes.Length)
            {
                throw new InvalidDataException("a LEB128 number runs past the end of the file");
            }

            byte b = bytes[position++];
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("a LEB128 number longer than 32 bits");
    }

    /// <summary>Reads a name: a LEB128 length, then that many UTF-8 bytes.</summary>
    /// <param name="bytes">The binary.</param>
    /// <param name="position">Where the name starts; moved past it.</param>
    /// <returns>The name.</returns>
    private static string ReadName(byte[] bytes, ref int position)
    {
        int length = (int)ReadUnsigned(bytes, ref position);
        string name = Encoding.UTF8.GetString(bytes, position, length);
        position += length;
        return name;
    }

    /// <summary>Skips an import's description, whose shape depends on its kind.</summary>
    /// <param name="bytes">The binary.</param>
    /// <param name="position">Where the description starts; moved past it.</param>
    /// <param name="kind">0 function (a type index), 1 table (a reference type and limits), 2 memory (limits), 3 global (a value type and mutability), 4 tag (an attribute and a type index).</param>
    /// <exception cref="InvalidDataException">An unknown kind: the rest of the section could not be read.</exception>
    private static void SkipImportDescription(byte[] bytes, ref int position, byte kind)
    {
        switch (kind)
        {
            case 0:
                ReadUnsigned(bytes, ref position);
                break;
            case 1:
                position++;
                SkipLimits(bytes, ref position);
                break;
            case 2:
                SkipLimits(bytes, ref position);
                break;
            case 3:
                position += 2;
                break;
            case 4:
                position++;
                ReadUnsigned(bytes, ref position);
                break;
            default:
                throw new InvalidDataException($"unknown import kind {kind}");
        }
    }

    /// <summary>Skips limits: a flags byte, the minimum, and the maximum when flag bit 0 says there is one.</summary>
    /// <param name="bytes">The binary.</param>
    /// <param name="position">Where the limits start; moved past them.</param>
    private static void SkipLimits(byte[] bytes, ref int position)
    {
        byte flags = bytes[position++];
        ReadUnsigned(bytes, ref position);
        if ((flags & 1) != 0)
        {
            ReadUnsigned(bytes, ref position);
        }
    }

    /// <summary>The kind byte as JavaScript's WebAssembly API names it.</summary>
    /// <param name="kind">The kind byte of an import or export.</param>
    /// <returns>function, table, memory, global, tag, or "kind N" for anything else.</returns>
    private static string KindName(byte kind) => kind switch
    {
        0 => "function",
        1 => "table",
        2 => "memory",
        3 => "global",
        4 => "tag",
        _ => $"kind {kind}",
    };
}
