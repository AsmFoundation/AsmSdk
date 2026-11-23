using System;
using System.IO;
using System.Text;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Reads one named section's bytes from an object file of any format the SDK's assemblers write:
/// COFF (win-x64, win-arm64), 64-bit little-endian ELF (Linux) and 64-bit Mach-O (macOS). Just
/// enough of each format to find a section by name; not a general object reader.
/// </summary>
/// <remarks>
/// <para>
/// Why a reader of its own rather than llvm-readobj: the System toolchain (AsmToolchain=System)
/// brings the machine's assembler and linker, not necessarily llvm-readobj, and starting a
/// process per object would cost more than reading a few hundred bytes. The SDK reads its own
/// API records this way (see <see cref="AsmReadApi"/>).
/// </para>
/// <para>
/// Each format names the section its own way: COFF and ELF by section name (".asmapi"), Mach-O
/// by segment and section ("__DWARF", "__asmapi"), the names asmsdk.inc and asmsdk-gnu.inc
/// write. A name longer than COFF's eight characters is looked up in the string table, as COFF
/// stores it ("/offset").
/// </para>
/// </remarks>
internal static class ObjectSections
{
    /// <summary>COFF's machine field for x86-64.</summary>
    private const ushort CoffMachineAmd64 = 0x8664;

    /// <summary>COFF's machine field for ARM64.</summary>
    private const ushort CoffMachineArm64 = 0xAA64;

    /// <summary>64-bit Mach-O's magic number, little-endian.</summary>
    private const uint MachOMagic64 = 0xFEEDFACF;

    /// <summary>Mach-O's LC_SEGMENT_64 load command.</summary>
    private const uint LcSegment64 = 0x19;

    /// <summary>
    /// Reads a section of an object file, found by its name in whichever format the file is.
    /// </summary>
    /// <param name="path">The object file.</param>
    /// <param name="name">The section's name in COFF and ELF (".asmapi").</param>
    /// <param name="machOSegment">The segment that holds it in Mach-O ("__DWARF").</param>
    /// <param name="machOSection">Its section name in Mach-O ("__asmapi").</param>
    /// <returns>The section's bytes; null when the object has no such section.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be opened.</exception>
    /// <exception cref="InvalidDataException">The file is none of the three formats, or its headers point outside it.</exception>
    internal static byte[]? Read(string path, string name, string machOSegment, string machOSection)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length >= 4 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F')
        {
            return ReadElf(data, name, path);
        }

        if (data.Length >= 4 && U32(data, 0) == MachOMagic64)
        {
            return ReadMachO(data, machOSegment, machOSection, path);
        }

        if (data.Length >= 20 && (U16(data, 0) == CoffMachineAmd64 || U16(data, 0) == CoffMachineArm64))
        {
            return ReadCoff(data, name, path);
        }

        throw new InvalidDataException($"{path} is not a COFF, ELF64 or Mach-O 64 object file");
    }

    /// <summary>Finds a section of an ELF64 little-endian object by name (through the section-name string table).</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="name">The section name.</param>
    /// <param name="path">The file, for messages.</param>
    /// <returns>The section's bytes, or null.</returns>
    /// <exception cref="InvalidDataException">Not 64-bit little-endian, or a header points outside the file.</exception>
    private static byte[]? ReadElf(byte[] data, string name, string path)
    {
        // e_ident: class 2 (64-bit), data 1 (little-endian). The SDK writes no other kind.
        if (data.Length < 64 || data[4] != 2 || data[5] != 1)
        {
            throw new InvalidDataException($"{path} is not a 64-bit little-endian ELF file");
        }

        ulong shoff = U64(data, 0x28);
        int shentsize = U16(data, 0x3A);
        int shnum = U16(data, 0x3C);
        int shstrndx = U16(data, 0x3E);
        // More than 0xFF00 sections moves the real counts into section 0 (extended numbering):
        // sh_size holds the count and sh_link the string table's index.
        if (shnum == 0 && shoff != 0)
        {
            shnum = checked((int)U64(data, Offset(shoff, 0x20, data, path)));
        }

        if (shstrndx == 0xFFFF)
        {
            shstrndx = (int)U32(data, Offset(shoff, 0x28, data, path));
        }

        if (shnum == 0)
        {
            return null;
        }

        int strings = Offset(shoff, (ulong)shstrndx * (ulong)shentsize, data, path);
        int stringsOffset = Offset(U64(data, strings + 0x18), 0, data, path);
        for (int i = 0; i < shnum; i++)
        {
            int header = Offset(shoff, (ulong)i * (ulong)shentsize, data, path);
            int nameOffset = (int)U32(data, header);
            if (CString(data, stringsOffset + nameOffset) == name)
            {
                // sh_type 8 (SHT_NOBITS) has no bytes in the file.
                if (U32(data, header + 4) == 8)
                {
                    return Array.Empty<byte>();
                }

                return Slice(data, U64(data, header + 0x18), U64(data, header + 0x20), path);
            }
        }

        return null;
    }

    /// <summary>Finds a section of a 64-bit Mach-O object by segment and section name.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="segment">The segment name.</param>
    /// <param name="section">The section name.</param>
    /// <param name="path">The file, for messages.</param>
    /// <returns>The section's bytes, or null.</returns>
    /// <exception cref="InvalidDataException">A load command points outside the file.</exception>
    /// <remarks>
    /// An object (MH_OBJECT) has one LC_SEGMENT_64 with an empty name that holds every section;
    /// each section carries its own segment name, which is what is compared.
    /// </remarks>
    private static byte[]? ReadMachO(byte[] data, string segment, string section, string path)
    {
        uint commands = U32(data, 16);
        int position = 32;
        for (uint c = 0; c < commands; c++)
        {
            if (position + 8 > data.Length)
            {
                throw new InvalidDataException($"{path}: a Mach-O load command runs past the end of the file");
            }

            uint command = U32(data, position);
            int size = (int)U32(data, position + 4);
            if (size < 8 || position + size > data.Length)
            {
                throw new InvalidDataException($"{path}: a Mach-O load command has a bad size ({size})");
            }

            if (command == LcSegment64)
            {
                uint sections = U32(data, position + 64);
                for (uint s = 0; s < sections; s++)
                {
                    int header = position + 72 + (int)s * 80;
                    if (header + 80 > position + size)
                    {
                        throw new InvalidDataException($"{path}: a Mach-O section header runs past its load command");
                    }

                    if (FixedName(data, header, 16) == section && FixedName(data, header + 16, 16) == segment)
                    {
                        return Slice(data, U32(data, header + 48), U64(data, header + 40), path);
                    }
                }
            }

            position += size;
        }

        return null;
    }

    /// <summary>Finds a section of a COFF object by name, resolving long names through the string table.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="name">The section name.</param>
    /// <param name="path">The file, for messages.</param>
    /// <returns>The section's bytes, or null.</returns>
    /// <exception cref="InvalidDataException">A header points outside the file.</exception>
    private static byte[]? ReadCoff(byte[] data, string name, string path)
    {
        int sections = U16(data, 2);
        uint symbolTable = U32(data, 8);
        uint symbols = U32(data, 12);
        int optionalHeader = U16(data, 16);
        int first = 20 + optionalHeader;
        for (int i = 0; i < sections; i++)
        {
            int header = first + i * 40;
            if (header + 40 > data.Length)
            {
                throw new InvalidDataException($"{path}: a COFF section header runs past the end of the file");
            }

            string sectionName = FixedName(data, header, 8);
            // "/123": the name is at offset 123 of the string table, which follows the symbol
            // table (18 bytes per symbol).
            if (sectionName.StartsWith("/", StringComparison.Ordinal) && int.TryParse(sectionName.Substring(1), out int stringOffset))
            {
                sectionName = CString(data, Offset(symbolTable, (ulong)symbols * 18 + (ulong)stringOffset, data, path));
            }

            if (sectionName == name)
            {
                return Slice(data, U32(data, header + 20), U32(data, header + 16), path);
            }
        }

        return null;
    }

    /// <summary>Copies [offset, offset + size) of the file, checked against its length.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="offset">The start.</param>
    /// <param name="size">The length.</param>
    /// <param name="path">The file, for messages.</param>
    /// <returns>A copy of the range.</returns>
    /// <exception cref="InvalidDataException">The range runs outside the file.</exception>
    private static byte[] Slice(byte[] data, ulong offset, ulong size, string path)
    {
        if (offset > (ulong)data.Length || size > (ulong)data.Length - offset)
        {
            throw new InvalidDataException($"{path}: a section's bytes run past the end of the file");
        }

        var slice = new byte[size];
        Array.Copy(data, (long)offset, slice, 0, (long)size);
        return slice;
    }

    /// <summary>A file offset from a base and a displacement, checked against the file's length.</summary>
    /// <param name="baseOffset">The base.</param>
    /// <param name="displacement">The displacement.</param>
    /// <param name="data">The file's bytes.</param>
    /// <param name="path">The file, for messages.</param>
    /// <returns>The offset, as an index.</returns>
    /// <exception cref="InvalidDataException">The offset is outside the file.</exception>
    private static int Offset(ulong baseOffset, ulong displacement, byte[] data, string path)
    {
        ulong offset = baseOffset + displacement;
        if (offset < baseOffset || offset >= (ulong)data.Length)
        {
            throw new InvalidDataException($"{path}: a header points outside the file (offset {offset})");
        }

        return (int)offset;
    }

    /// <summary>A NUL-padded fixed-width name (COFF's 8 bytes, Mach-O's 16).</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="offset">The field.</param>
    /// <param name="width">The field's width.</param>
    /// <returns>The name, without the padding.</returns>
    private static string FixedName(byte[] data, int offset, int width)
    {
        int length = 0;
        while (length < width && data[offset + length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(data, offset, length);
    }

    /// <summary>A NUL-terminated string.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="offset">Its first byte.</param>
    /// <returns>The string (to the end of the file when no NUL follows).</returns>
    private static string CString(byte[] data, int offset)
    {
        int end = offset;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    /// <summary>A little-endian 16-bit value.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where it starts.</param>
    /// <returns>The value.</returns>
    private static ushort U16(byte[] data, int offset) => (ushort)(data[offset] | (data[offset + 1] << 8));

    /// <summary>A little-endian 32-bit value.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where it starts.</param>
    /// <returns>The value.</returns>
    private static uint U32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    /// <summary>A little-endian 64-bit value.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where it starts.</param>
    /// <returns>The value.</returns>
    private static ulong U64(byte[] data, int offset) => U32(data, offset) | ((ulong)U32(data, offset + 4) << 32);
}
