using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// Checks a linked Windows or macOS image for load-time fixups its loader cannot apply, which
/// the bundled linker accepts without a word: 32-bit absolute addresses in a 64-bit PE image
/// (ASM0080), and fixups inside a read-only Mach-O segment, the "text relocations" (ASM0081).
/// </summary>
/// <remarks>
/// <para>
/// Both come from assembly that addresses memory absolutely, which every 64-bit OS discourages
/// and only Linux's linker refuses outright (ld.lld: "recompile with -fPIC", measured). Measured
/// with the bundled lld 23.1.5 and NASM 3.02:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Windows: a NASM memory operand without <c>default rel</c> (<c>lea rax, [msg]</c>) becomes an
/// IMAGE_REL_AMD64_ADDR32 relocation. lld-link links it into a DLL whose image base is
/// 0x180000000, with a HIGHLOW base relocation: the 32-bit field cannot hold the address, so the
/// code computes a wrong one at run time. MSVC's linker refuses the same input (LNK2017). NASM
/// warns ("32-bit absolute section-crossing relocation"), but making that warning an error is
/// not an option: NASM gives it for <c>dd label wrt ..imagebase</c> too, the image-relative
/// form that Windows unwind tables (.pdata, .xdata) need.
/// </description></item>
/// <item><description>
/// macOS: <c>mov rax, msg</c> (a 64-bit absolute address in code) links into a dylib with a
/// rebase entry in __TEXT, which is mapped read-only. Apple's ld refuses that ("illegal
/// text-relocation"). ld64.lld writes it.
/// </description></item>
/// </list>
/// <para>
/// What is checked: the base relocation table of a PE32+ image (HIGHLOW entries, type 3); the
/// rebase, bind and weak-bind opcodes of LC_DYLD_INFO(_ONLY), or the page starts of
/// LC_DYLD_CHAINED_FIXUPS, of a 64-bit Mach-O image. A PE32+ image that cannot load above
/// 2 GB (no DYNAMIC_BASE, and its image base plus size within 2 GB, which only explicit
/// linker arguments give) may keep 32-bit addresses: they are valid there. 32-bit images and
/// ELF are not checked: AsmSdk builds none of the former, and ld.lld refuses both cases itself.
/// </para>
/// <para>
/// Footgun: a failed check leaves the image on disk; the caller must delete it, or the next
/// incremental build sees an up-to-date output and skips the link (and this check) entirely.
/// AsmFoundation.Build.targets does so.
/// </para>
/// </remarks>
public sealed class AsmCheckImage : Task
{
    /// <summary>IMAGE_REL_BASED_HIGHLOW: a 32-bit absolute address the loader adjusts.</summary>
    private const int ImageRelBasedHighLow = 3;

    /// <summary>IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE: the image may load at any address (ASLR).</summary>
    private const ushort DynamicBase = 0x40;

    /// <summary>The PE32+ optional header magic; a PE32 (32-bit) image has 0x10B.</summary>
    private const ushort Pe32PlusMagic = 0x20B;

    /// <summary>The highest address a sign-extended 32-bit displacement reaches.</summary>
    private const ulong TwoGigabytes = 0x80000000UL;

    /// <summary>MH_MAGIC_64: a 64-bit little-endian Mach-O file.</summary>
    private const uint MachOMagic64 = 0xFEEDFACF;

    /// <summary>LC_SEGMENT_64.</summary>
    private const uint LcSegment64 = 0x19;

    /// <summary>LC_DYLD_INFO.</summary>
    private const uint LcDyldInfo = 0x22;

    /// <summary>LC_DYLD_INFO_ONLY (LC_DYLD_INFO with LC_REQ_DYLD).</summary>
    private const uint LcDyldInfoOnly = 0x80000022;

    /// <summary>LC_DYLD_CHAINED_FIXUPS.</summary>
    private const uint LcDyldChainedFixups = 0x80000034;

    /// <summary>VM_PROT_WRITE in a segment's initial protection.</summary>
    private const int VmProtWrite = 2;

    /// <summary>DYLD_CHAINED_PTR_START_NONE: a page of the segment with no fixups.</summary>
    private const ushort ChainedPtrStartNone = 0xFFFF;

    /// <summary>How many offending addresses one message lists before it counts the rest.</summary>
    private const int ListedAddresses = 5;

    /// <summary>The linked image: a .dll or .exe for Windows, a .dylib or program for macOS.</summary>
    [Required]
    public string ImagePath { get; set; } = "";

    /// <summary>
    /// The OS of the image, as AsmSdk names it: <c>Windows</c> or <c>MacOS</c>. Anything else
    /// (Linux) checks nothing and succeeds.
    /// </summary>
    [Required]
    public string Os { get; set; } = "";

    /// <summary>Reads the image and checks its fixups.</summary>
    /// <returns>
    /// True when the loader can apply every fixup (or the OS is not checked); false after logging
    /// ASM0080 (a 32-bit absolute address in a PE32+ image), ASM0081 (a fixup in a read-only
    /// Mach-O segment) or ASM0082 (the image cannot be read, or is not the format its OS uses).
    /// </returns>
    public override bool Execute()
    {
        if (Os != "Windows" && Os != "MacOS")
        {
            return true;
        }

        byte[] image;
        try
        {
            image = File.ReadAllBytes(ImagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "ASM0082", null, ImagePath, 0, 0, 0, 0, "Cannot read the linked image {0} to check its fixups: {1}", ImagePath, ex.Message);
            return false;
        }

        try
        {
            return Os == "Windows" ? CheckPe(image) : CheckMachO(image);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
        {
            // A truncated or foreign file: the bounds checks of the readers below end up here
            // rather than reporting a wrong verdict.
            Log.LogError(null, "ASM0082", null, ImagePath, 0, 0, 0, 0, "Cannot check the fixups of {0}: it is not a well-formed {1} image ({2}).", ImagePath, Os == "Windows" ? "PE32+" : "64-bit Mach-O", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Checks a PE image: every HIGHLOW base relocation of a PE32+ image that may load above
    /// 2 GB is an error.
    /// </summary>
    /// <param name="image">The file's bytes.</param>
    /// <returns>True when the image has no such relocation, or may keep them; false after ASM0080 or ASM0082.</returns>
    /// <exception cref="IndexOutOfRangeException">The file is shorter than its headers say.</exception>
    /// <exception cref="FormatException">A header field points outside the file.</exception>
    private bool CheckPe(byte[] image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            Log.LogError(null, "ASM0082", null, ImagePath, 0, 0, 0, 0, "Cannot check the fixups of {0}: it is not a PE image.", ImagePath);
            return false;
        }

        int pe = (int)U32(image, 0x3C);
        if (U32(image, pe) != 0x00004550)
        {
            Log.LogError(null, "ASM0082", null, ImagePath, 0, 0, 0, 0, "Cannot check the fixups of {0}: it has no PE signature.", ImagePath);
            return false;
        }

        int coff = pe + 4;
        int sectionCount = U16(image, coff + 2);
        int optionalHeaderSize = U16(image, coff + 16);
        int optional = coff + 20;
        if (U16(image, optional) != Pe32PlusMagic)
        {
            // PE32: 32-bit addresses are its normal form, and AsmSdk builds no such image.
            return true;
        }

        ulong imageBase = U64(image, optional + 24);
        uint imageSize = U32(image, optional + 56);
        ushort dllCharacteristics = U16(image, optional + 70);
        if ((dllCharacteristics & DynamicBase) == 0 && imageBase + imageSize <= TwoGigabytes)
        {
            // Fixed below 2 GB (only explicit linker arguments do that): a 32-bit address holds.
            return true;
        }

        uint directoryCount = U32(image, optional + 108);
        if (directoryCount <= 5)
        {
            return true;
        }

        // Data directory 5: the base relocation table.
        uint relocationRva = U32(image, optional + 112 + (5 * 8));
        uint relocationSize = U32(image, optional + 112 + (5 * 8) + 4);
        if (relocationRva == 0 || relocationSize == 0)
        {
            return true;
        }

        var sections = new List<(string Name, uint Rva, uint Size, uint FileOffset)>();
        int sectionTable = optional + optionalHeaderSize;
        for (int i = 0; i < sectionCount; i++)
        {
            int header = sectionTable + (i * 40);
            string name = Encoding.ASCII.GetString(image, header, 8).TrimEnd('\0');
            uint virtualSize = U32(image, header + 8);
            uint rva = U32(image, header + 12);
            uint rawSize = U32(image, header + 16);
            uint rawOffset = U32(image, header + 20);
            sections.Add((name, rva, Math.Max(virtualSize, rawSize), rawOffset));
        }

        int table = FileOffset(sections, relocationRva);
        var found = new List<string>();
        int total = 0;
        int position = 0;
        while (position + 8 <= relocationSize)
        {
            uint pageRva = U32(image, table + position);
            uint blockSize = U32(image, table + position + 4);
            if (blockSize < 8)
            {
                // A zero-sized block would never advance: the table is malformed.
                throw new FormatException($"a base relocation block of {blockSize} bytes");
            }

            for (int entry = 8; entry + 2 <= blockSize; entry += 2)
            {
                ushort value = U16(image, table + position + entry);
                if (value >> 12 != ImageRelBasedHighLow)
                {
                    continue;
                }

                total++;
                if (found.Count < ListedAddresses)
                {
                    uint rva = pageRva + (uint)(value & 0xFFF);
                    found.Add(Describe(sections, rva));
                }
            }

            position += (int)blockSize;
        }

        if (total == 0)
        {
            return true;
        }

        Log.LogError(null, "ASM0080", null, ImagePath, 0, 0, 0, 0,
            "{0} needs {1} load-time fixup(s) of a 32-bit absolute address ({2}{3}). A 64-bit Windows image loads above 4 GB (image base 0x{4:X}, ASLR), where no 32-bit address reaches it, so the code would compute a wrong address. NASM writes such an address for a memory operand in a file without \"default rel\" (lea rax, [label]) and for \"dd label\"; its warning \"32-bit absolute section-crossing relocation\" names each line. Put \"default rel\" at the top of the source, and use \"dq label\" in data tables. (lld-link links this without a word; Microsoft's linker refuses it: LNK2017.)",
            Path.GetFileName(ImagePath), total, string.Join(", ", found), total > found.Count ? ", ..." : "", imageBase);
        return false;
    }

    /// <summary>Names an RVA by its section, for a message: <c>.text+0x4</c>.</summary>
    /// <param name="sections">The image's sections.</param>
    /// <param name="rva">The relative virtual address.</param>
    /// <returns>The section and offset, or the bare RVA when no section holds it.</returns>
    private static string Describe(List<(string Name, uint Rva, uint Size, uint FileOffset)> sections, uint rva)
    {
        foreach (var section in sections)
        {
            if (rva >= section.Rva && rva < section.Rva + section.Size)
            {
                return $"{section.Name}+0x{rva - section.Rva:X}";
            }
        }

        return $"RVA 0x{rva:X}";
    }

    /// <summary>The file offset of an RVA, through the section that holds it.</summary>
    /// <param name="sections">The image's sections.</param>
    /// <param name="rva">The relative virtual address.</param>
    /// <returns>The offset in the file.</returns>
    /// <exception cref="FormatException">No section holds the RVA.</exception>
    private static int FileOffset(List<(string Name, uint Rva, uint Size, uint FileOffset)> sections, uint rva)
    {
        foreach (var section in sections)
        {
            if (rva >= section.Rva && rva < section.Rva + section.Size)
            {
                return (int)(section.FileOffset + (rva - section.Rva));
            }
        }

        throw new FormatException($"no section holds RVA 0x{rva:X}");
    }

    /// <summary>
    /// Checks a 64-bit Mach-O image: every rebase, bind or chained fixup in a segment the image
    /// maps without write permission is an error.
    /// </summary>
    /// <param name="image">The file's bytes.</param>
    /// <returns>True when every fixup is in a writable segment; false after ASM0081 or ASM0082.</returns>
    /// <exception cref="IndexOutOfRangeException">The file is shorter than its load commands say.</exception>
    /// <exception cref="FormatException">An opcode stream or a fixup table is malformed.</exception>
    private bool CheckMachO(byte[] image)
    {
        if (image.Length < 32 || U32(image, 0) != MachOMagic64)
        {
            Log.LogError(null, "ASM0082", null, ImagePath, 0, 0, 0, 0, "Cannot check the fixups of {0}: it is not a 64-bit Mach-O image.", ImagePath);
            return false;
        }

        uint commandCount = U32(image, 16);
        var segments = new List<(string Name, ulong Address, bool Writable)>();
        var streams = new List<(string Kind, uint Offset, uint Size)>();
        (uint Offset, uint Size)? chained = null;
        int command = 32;
        for (uint i = 0; i < commandCount; i++)
        {
            uint kind = U32(image, command);
            uint size = U32(image, command + 4);
            if (size < 8)
            {
                throw new FormatException($"a load command of {size} bytes");
            }

            if (kind == LcSegment64)
            {
                string name = Encoding.ASCII.GetString(image, command + 8, 16).TrimEnd('\0');
                ulong address = U64(image, command + 24);
                int initialProtection = (int)U32(image, command + 60);
                segments.Add((name, address, (initialProtection & VmProtWrite) != 0));
            }
            else if (kind == LcDyldInfo || kind == LcDyldInfoOnly)
            {
                // rebase, bind, weak bind (lazy binds target __la_symbol_ptr, always writable).
                streams.Add(("rebase", U32(image, command + 8), U32(image, command + 12)));
                streams.Add(("bind", U32(image, command + 16), U32(image, command + 20)));
                streams.Add(("weak bind", U32(image, command + 24), U32(image, command + 28)));
            }
            else if (kind == LcDyldChainedFixups)
            {
                chained = (U32(image, command + 8), U32(image, command + 12));
            }

            command += (int)size;
        }

        var found = new List<string>();
        int total = 0;
        void Report(int segment, ulong offset, string kind, string? symbol)
        {
            if (segment < 0 || segment >= segments.Count)
            {
                throw new FormatException($"a fixup in segment {segment} of {segments.Count}");
            }

            if (segments[segment].Writable)
            {
                return;
            }

            total++;
            if (found.Count < ListedAddresses)
            {
                found.Add($"{kind}{(symbol is null ? "" : " of " + symbol)} at 0x{segments[segment].Address + offset:X} in {segments[segment].Name}");
            }
        }

        foreach (var stream in streams)
        {
            if (stream.Size == 0)
            {
                continue;
            }

            if (stream.Kind == "rebase")
            {
                DecodeRebases(image, (int)stream.Offset, (int)stream.Size, (segment, offset) => Report(segment, offset, "rebase", null));
            }
            else
            {
                DecodeBinds(image, (int)stream.Offset, (int)stream.Size, (segment, offset, symbol) => Report(segment, offset, stream.Kind, symbol));
            }
        }

        if (chained is { } fixups && fixups.Size > 0)
        {
            DecodeChainedStarts(image, (int)fixups.Offset, (segment, page, pageSize) => Report(segment, (ulong)page * pageSize, "chained fixups on the page", null));
        }

        if (total == 0)
        {
            return true;
        }

        Log.LogError(null, "ASM0081", null, ImagePath, 0, 0, 0, 0,
            "{0} needs {1} load-time fixup(s) in a read-only segment ({2}{3}): an absolute address stored in code, which dyld would have to write into memory mapped without write access (a text relocation). Apple's linker refuses these (\"illegal text-relocation\"). In NASM, write lea rax, [rel label] instead of mov rax, label; in GNU syntax, use ADDRESS instead of a .quad or an ldr =label in __text; keep pointer tables in .data.",
            Path.GetFileName(ImagePath), total, string.Join(", ", found), total > found.Count ? ", ..." : "");
        return false;
    }

    /// <summary>Decodes Mach-O rebase opcodes, reporting each rebased pointer.</summary>
    /// <param name="image">The file's bytes.</param>
    /// <param name="start">The stream's file offset.</param>
    /// <param name="size">The stream's size in bytes.</param>
    /// <param name="rebase">Called with the segment index and the offset in it of each pointer.</param>
    /// <exception cref="FormatException">An unknown opcode.</exception>
    /// <exception cref="IndexOutOfRangeException">The stream runs past the file.</exception>
    private static void DecodeRebases(byte[] image, int start, int size, Action<int, ulong> rebase)
    {
        const ulong pointerSize = 8;
        int position = start;
        int end = start + size;
        int segment = 0;
        ulong offset = 0;
        while (position < end)
        {
            byte value = image[position++];
            int opcode = value & 0xF0;
            int immediate = value & 0x0F;
            switch (opcode)
            {
                case 0x00: // REBASE_OPCODE_DONE
                    return;
                case 0x10: // REBASE_OPCODE_SET_TYPE_IMM
                    break;
                case 0x20: // REBASE_OPCODE_SET_SEGMENT_AND_OFFSET_ULEB
                    segment = immediate;
                    offset = Uleb(image, ref position);
                    break;
                case 0x30: // REBASE_OPCODE_ADD_ADDR_ULEB
                    offset += Uleb(image, ref position);
                    break;
                case 0x40: // REBASE_OPCODE_ADD_ADDR_IMM_SCALED
                    offset += (ulong)immediate * pointerSize;
                    break;
                case 0x50: // REBASE_OPCODE_DO_REBASE_IMM_TIMES
                    for (int i = 0; i < immediate; i++)
                    {
                        rebase(segment, offset);
                        offset += pointerSize;
                    }

                    break;
                case 0x60: // REBASE_OPCODE_DO_REBASE_ULEB_TIMES
                {
                    ulong count = Uleb(image, ref position);
                    for (ulong i = 0; i < count; i++)
                    {
                        rebase(segment, offset);
                        offset += pointerSize;
                    }

                    break;
                }

                case 0x70: // REBASE_OPCODE_DO_REBASE_ADD_ADDR_ULEB
                    rebase(segment, offset);
                    offset += pointerSize + Uleb(image, ref position);
                    break;
                case 0x80: // REBASE_OPCODE_DO_REBASE_ULEB_TIMES_SKIPPING_ULEB
                {
                    ulong count = Uleb(image, ref position);
                    ulong skip = Uleb(image, ref position);
                    for (ulong i = 0; i < count; i++)
                    {
                        rebase(segment, offset);
                        offset += pointerSize + skip;
                    }

                    break;
                }

                default:
                    throw new FormatException($"rebase opcode 0x{opcode:X2}");
            }
        }
    }

    /// <summary>Decodes Mach-O bind (or weak bind) opcodes, reporting each bound pointer.</summary>
    /// <param name="image">The file's bytes.</param>
    /// <param name="start">The stream's file offset.</param>
    /// <param name="size">The stream's size in bytes.</param>
    /// <param name="bind">Called with the segment index, the offset in it and the symbol of each pointer.</param>
    /// <exception cref="FormatException">An unknown opcode.</exception>
    /// <exception cref="IndexOutOfRangeException">The stream runs past the file.</exception>
    private static void DecodeBinds(byte[] image, int start, int size, Action<int, ulong, string> bind)
    {
        const ulong pointerSize = 8;
        int position = start;
        int end = start + size;
        int segment = 0;
        ulong offset = 0;
        string symbol = "";
        while (position < end)
        {
            byte value = image[position++];
            int opcode = value & 0xF0;
            int immediate = value & 0x0F;
            switch (opcode)
            {
                case 0x00: // BIND_OPCODE_DONE: ends one bind record; more may follow
                    break;
                case 0x10: // BIND_OPCODE_SET_DYLIB_ORDINAL_IMM
                case 0x30: // BIND_OPCODE_SET_DYLIB_SPECIAL_IMM
                case 0x50: // BIND_OPCODE_SET_TYPE_IMM
                    break;
                case 0x20: // BIND_OPCODE_SET_DYLIB_ORDINAL_ULEB
                    Uleb(image, ref position);
                    break;
                case 0x40: // BIND_OPCODE_SET_SYMBOL_TRAILING_FLAGS_IMM, then a C string
                {
                    int nameStart = position;
                    while (image[position] != 0)
                    {
                        position++;
                    }

                    symbol = Encoding.ASCII.GetString(image, nameStart, position - nameStart);
                    position++;
                    break;
                }

                case 0x60: // BIND_OPCODE_SET_ADDEND_SLEB
                    Uleb(image, ref position);
                    break;
                case 0x70: // BIND_OPCODE_SET_SEGMENT_AND_OFFSET_ULEB
                    segment = immediate;
                    offset = Uleb(image, ref position);
                    break;
                case 0x80: // BIND_OPCODE_ADD_ADDR_ULEB
                    offset += Uleb(image, ref position);
                    break;
                case 0x90: // BIND_OPCODE_DO_BIND
                    bind(segment, offset, symbol);
                    offset += pointerSize;
                    break;
                case 0xA0: // BIND_OPCODE_DO_BIND_ADD_ADDR_ULEB
                    bind(segment, offset, symbol);
                    offset += pointerSize + Uleb(image, ref position);
                    break;
                case 0xB0: // BIND_OPCODE_DO_BIND_ADD_ADDR_IMM_SCALED
                    bind(segment, offset, symbol);
                    offset += pointerSize + ((ulong)immediate * pointerSize);
                    break;
                case 0xC0: // BIND_OPCODE_DO_BIND_ULEB_TIMES_SKIPPING_ULEB
                {
                    ulong count = Uleb(image, ref position);
                    ulong skip = Uleb(image, ref position);
                    for (ulong i = 0; i < count; i++)
                    {
                        bind(segment, offset, symbol);
                        offset += pointerSize + skip;
                    }

                    break;
                }

                default:
                    // 0xD0, BIND_OPCODE_THREADED, is arm64e's, which AsmSdk does not build.
                    throw new FormatException($"bind opcode 0x{opcode:X2}");
            }
        }
    }

    /// <summary>
    /// Decodes the starts of an LC_DYLD_CHAINED_FIXUPS blob: reports each page of each segment
    /// where a chain of fixups begins.
    /// </summary>
    /// <param name="image">The file's bytes.</param>
    /// <param name="start">The blob's file offset (dyld_chained_fixups_header).</param>
    /// <param name="page">Called with the segment index, the page index and the page size of each page with fixups.</param>
    /// <exception cref="IndexOutOfRangeException">A table runs past the file.</exception>
    private static void DecodeChainedStarts(byte[] image, int start, Action<int, int, ulong> page)
    {
        // dyld_chained_fixups_header: fixups_version, starts_offset, ...
        int starts = start + (int)U32(image, start + 4);

        // dyld_chained_starts_in_image: seg_count, then seg_info_offset[seg_count] (0: none).
        int segmentCount = (int)U32(image, starts);
        for (int segment = 0; segment < segmentCount; segment++)
        {
            uint info = U32(image, starts + 4 + (segment * 4));
            if (info == 0)
            {
                continue;
            }

            // dyld_chained_starts_in_segment: size, page_size (u16), pointer_format (u16),
            // segment_offset (u64), max_valid_pointer (u32), page_count (u16), page_start[].
            int segmentStarts = starts + (int)info;
            ulong pageSize = U16(image, segmentStarts + 4);
            int pageCount = U16(image, segmentStarts + 20);
            for (int i = 0; i < pageCount; i++)
            {
                if (U16(image, segmentStarts + 22 + (i * 2)) != ChainedPtrStartNone)
                {
                    page(segment, i, pageSize);
                }
            }
        }
    }

    /// <summary>Reads an unsigned LEB128 number (SLEB128 values are skipped the same way).</summary>
    /// <param name="image">The file's bytes.</param>
    /// <param name="position">Where the number starts; moved past it.</param>
    /// <returns>The value (bits beyond 64 dropped).</returns>
    /// <exception cref="IndexOutOfRangeException">The number runs past the file.</exception>
    private static ulong Uleb(byte[] image, ref int position)
    {
        ulong value = 0;
        int shift = 0;
        byte current;
        do
        {
            current = image[position++];
            if (shift < 64)
            {
                value |= (ulong)(current & 0x7F) << shift;
            }

            shift += 7;
        }
        while ((current & 0x80) != 0);
        return value;
    }

    /// <summary>A little-endian 16-bit value (independent of the build machine's byte order).</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where the value starts.</param>
    /// <returns>The value.</returns>
    /// <exception cref="IndexOutOfRangeException">The value runs past the bytes.</exception>
    private static ushort U16(byte[] data, int offset) => (ushort)(data[offset] | (data[offset + 1] << 8));

    /// <summary>A little-endian 32-bit value.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where the value starts.</param>
    /// <returns>The value.</returns>
    /// <exception cref="IndexOutOfRangeException">The value runs past the bytes.</exception>
    private static uint U32(byte[] data, int offset) =>
        data[offset] | ((uint)data[offset + 1] << 8) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);

    /// <summary>A little-endian 64-bit value.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where the value starts.</param>
    /// <returns>The value.</returns>
    /// <exception cref="IndexOutOfRangeException">The value runs past the bytes.</exception>
    private static ulong U64(byte[] data, int offset) => U32(data, offset) | ((ulong)U32(data, offset + 4) << 32);
}
