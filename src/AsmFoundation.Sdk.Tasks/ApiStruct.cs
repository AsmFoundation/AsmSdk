using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>A STRUCT field, laid out: its declaration and where it lands.</summary>
/// <param name="declaration">The field as parsed.</param>
/// <param name="offset">Its offset from the struct's start, in bytes.</param>
/// <param name="size">Its size in bytes (an array's whole size).</param>
internal sealed class ApiStructField(ApiFieldDeclaration declaration, int offset, int size)
{
    /// <summary>The field's name.</summary>
    internal string Name => declaration.Name;

    /// <summary>The field's type (an array's element type).</summary>
    internal ApiType Type => declaration.Type;

    /// <summary>The array's element count, or null for a single value.</summary>
    internal int? Count => declaration.Count;

    /// <summary>The offset from the struct's start, in bytes.</summary>
    internal int Offset { get; } = offset;

    /// <summary>The size in bytes (an array's whole size).</summary>
    internal int Size { get; } = size;

    /// <summary>The canonical C spelling: "uint32_t crc", "uint32_t h[8]", "int32_t (*cmp)(int32_t, int32_t)".</summary>
    /// <returns>The declaration.</returns>
    internal string ToC() => Type.ToC(Name) + (Count is int count ? "[" + count.ToString(CultureInfo.InvariantCulture) + "]" : "");
}

/// <summary>
/// A STRUCT line, parsed and laid out as C lays it out on the RID: each field at the next offset
/// its alignment allows, the struct as large as its last field's end rounded up to its largest
/// alignment. One computation for everything that depends on it: the offsets the assembly code
/// reads (the constants files), the layout the API file records and the RIDs are compared on,
/// and the C# struct the generator writes from it.
/// </summary>
/// <remarks>
/// <para>
/// Why C's natural alignment answers every RID the API covers: Windows x64, System V x86-64 and
/// AAPCS64 (Linux, Windows and macOS on ARM64) align each of these types to its own size, and a
/// struct to its largest member's alignment (measured: .NET's sequential layout of the same
/// fields on win-x64, a bool, a pointer, a fixed buffer, a nested struct and a function pointer,
/// lands every field at C's offset). Pointer-sized types take the RID's pointer size, passed in:
/// 8 on every RID with an API today (WebAssembly takes no part yet; wasm32 would pass 4).
/// </para>
/// <para>
/// What a field may be (the owner's choice of 2026-11-24): a PROTO type (scalars, pointers,
/// function pointers), a STRUCT declared before (by value, laid out inline), or a fixed array
/// of a primitive scalar. Refused with the reason (ASM0130 at the line): arrays of anything else,
/// because C#'s fixed buffers hold primitive types only (bool, the fixed-width integers, float
/// and double: no pointers, no size_t, no structs); a struct holding itself by value; a struct
/// over <see cref="MaxSize"/> bytes.
/// </para>
/// </remarks>
internal sealed class ApiStruct
{
    /// <summary>The largest struct the SDK lays out: offsets stay far from int overflow, and a struct this large is a buffer, not an ABI type.</summary>
    internal const int MaxSize = 1 << 24;

    /// <summary>The C scalar types whose size does not depend on the RID, with their size (also their alignment).</summary>
    private static readonly Dictionary<string, int> FixedSizes = new(StringComparer.Ordinal)
    {
        ["bool"] = 1, ["char"] = 1, ["int8_t"] = 1, ["uint8_t"] = 1,
        ["int16_t"] = 2, ["uint16_t"] = 2,
        ["int32_t"] = 4, ["uint32_t"] = 4, ["float"] = 4,
        ["int64_t"] = 8, ["uint64_t"] = 8, ["double"] = 8,
    };

    /// <summary>The C scalar types as wide as a pointer on the RID: no C# fixed buffer can hold them.</summary>
    private static readonly HashSet<string> PointerSized = new(StringComparer.Ordinal) { "size_t", "ptrdiff_t", "intptr_t", "uintptr_t" };

    /// <summary>Creates a laid-out struct.</summary>
    /// <param name="name">The struct's name.</param>
    /// <param name="fields">Its fields, laid out, in order.</param>
    /// <param name="size">Its size in bytes, a multiple of <paramref name="alignment"/>.</param>
    /// <param name="alignment">Its alignment: its largest field's.</param>
    private ApiStruct(string name, IReadOnlyList<ApiStructField> fields, int size, int alignment)
    {
        Name = name;
        Fields = fields;
        Size = size;
        Alignment = alignment;
    }

    /// <summary>The struct's name: its constants' prefix and, in PascalCase, its C# name.</summary>
    internal string Name { get; }

    /// <summary>The fields, in order, with their offsets.</summary>
    internal IReadOnlyList<ApiStructField> Fields { get; }

    /// <summary>The size in bytes, padding included (C's sizeof).</summary>
    internal int Size { get; }

    /// <summary>The alignment in bytes (C's alignof).</summary>
    internal int Alignment { get; }

    /// <summary>
    /// The line in one spelling ("name, type field, type field[8]"): two declarations of the same
    /// struct have the same canonical form whatever their spacing, so the comparisons (two
    /// sources, two RIDs) compare this.
    /// </summary>
    internal string Canonical => Name + ", " + string.Join(", ", Fields.Select(f => f.ToC()));

    /// <summary>Parses a STRUCT line and lays it out for a RID.</summary>
    /// <param name="text">The line (the text after "STRUCT").</param>
    /// <param name="pointerSize">The RID's pointer size in bytes (8 on every RID with an API today).</param>
    /// <param name="declared">The STRUCTs declared before this line, by name (null when unknown): a field may hold one by value, or point at one.</param>
    /// <returns>The struct.</returns>
    /// <exception cref="FormatException">The line is not a struct this SDK lays out; the message says what to write instead.</exception>
    internal static ApiStruct Parse(string text, int pointerSize, Func<string, ApiStruct?> declared)
    {
        (string name, IReadOnlyList<ApiFieldDeclaration> declarations) = ApiPrototype.ParseStruct(text, n => declared(n) is not null);
        var fields = new List<ApiStructField>(declarations.Count);
        int offset = 0;
        int alignment = 1;
        foreach (ApiFieldDeclaration field in declarations)
        {
            (int elementSize, int elementAlignment) = Measure(name, field, pointerSize, declared);
            long size = (long)elementSize * (field.Count ?? 1);
            // Natural alignment: the field starts at the next multiple of its alignment.
            offset = AlignUp(offset, elementAlignment);
            if (offset + size > MaxSize)
            {
                throw new FormatException($"{name} would be over {MaxSize} bytes: keep large buffers out of the struct (a pointer and a length)");
            }

            fields.Add(new ApiStructField(field, offset, (int)size));
            offset += (int)size;
            alignment = Math.Max(alignment, elementAlignment);
        }

        // The tail padding: an array of the struct keeps every element aligned.
        return new ApiStruct(name, fields, AlignUp(offset, alignment), alignment);
    }

    /// <summary>The size and alignment of one element of a field, checking what an array may hold.</summary>
    /// <param name="structName">The struct being laid out, for messages and the self-containment check.</param>
    /// <param name="field">The field.</param>
    /// <param name="pointerSize">The RID's pointer size.</param>
    /// <param name="declared">The STRUCTs declared before.</param>
    /// <returns>The element's size and alignment.</returns>
    /// <exception cref="FormatException">The field holds its own struct by value, or is an array of a type no C# fixed buffer holds.</exception>
    private static (int Size, int Alignment) Measure(string structName, ApiFieldDeclaration field, int pointerSize, Func<string, ApiStruct?> declared)
    {
        ApiType type = field.Type;
        bool pointer = type.Callback is not null || type.Pointers > 0;
        if (field.Count is not null && (pointer || type.IsStruct || PointerSized.Contains(type.Base)))
        {
            // C# fixed buffers (the generated C# of an array field) take bool, the fixed-width
            // integers, float and double only: refused here, at the asm line, rather than as a
            // compiler error in generated code.
            string what = type.Callback is not null ? "function pointers"
                : pointer ? "pointers"
                : type.IsStruct ? $"structs ({type.Base})"
                : $"{type.Base} (pointer-sized)";
            throw new FormatException($"{field.Name}[{field.Count}] is an array of {what}, and C#'s fixed buffers hold bool, the fixed-width integers, float and double only: use one of those (uint64_t for a pointer-sized value), or one field per element");
        }

        if (pointer)
        {
            return (pointerSize, pointerSize);
        }

        if (type.IsStruct)
        {
            if (type.Base == structName)
            {
                throw new FormatException($"{structName} holds itself by value ({field.Name}), which would make it infinitely large: point at it instead ({structName}* {field.Name})");
            }

            ApiStruct inner = declared(type.Base) ?? throw new FormatException($"{type.Base} ({field.Name}) is not declared before {structName}: declare it first, as C would");
            return (inner.Size, inner.Alignment);
        }

        if (PointerSized.Contains(type.Base))
        {
            return (pointerSize, pointerSize);
        }

        // The parser lets only known C types through, and void without a pointer is refused
        // there, so every remaining base has a fixed size.
        int size = FixedSizes[type.Base];
        return (size, size);
    }

    /// <summary>Rounds an offset up to a multiple of an alignment (a power of two, or 1).</summary>
    /// <param name="offset">The offset.</param>
    /// <param name="alignment">The alignment.</param>
    /// <returns>The aligned offset.</returns>
    private static int AlignUp(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    /// <summary>The struct as the API file writes it.</summary>
    /// <returns>{"name", "declaration", "size", "alignment", "fields": [{"name", "type", "offset", "count"?}]}.</returns>
    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json["name"] = new JsonString(Name);
        json["declaration"] = new JsonString(Canonical);
        json["size"] = Number(Size);
        json["alignment"] = Number(Alignment);
        var fields = new JsonArray();
        foreach (ApiStructField field in Fields)
        {
            var entry = new JsonObject();
            entry["name"] = new JsonString(field.Name);
            entry["type"] = field.Type.ToJson();
            entry["offset"] = Number(field.Offset);
            if (field.Count is int count)
            {
                entry["count"] = Number(count);
            }

            fields.Items.Add(entry);
        }

        json["fields"] = fields;
        return json;
    }

    /// <summary>
    /// The constants an assembly source reads the structs through, as NASM preprocessor numbers:
    /// <c>%assign rect.min 0</c> per field and <c>%assign rect_size 20</c> per struct, the names
    /// NASM's own <c>struc</c> gives. Numbers rather than <c>equ</c> labels, so that a size works
    /// in <c>%if</c> and <c>times</c> too, and nothing lands in the object's symbol table
    /// (measured on elf64, win64 and macho64).
    /// </summary>
    /// <param name="structs">The structs, in declaration order.</param>
    /// <returns>The file's text.</returns>
    internal static string ToNasm(IEnumerable<ApiStruct> structs)
    {
        var text = new StringBuilder();
        text.Append("; Generated by AsmFoundation.Sdk from the STRUCT lines of this source and its includes. Do not edit; edit the STRUCT lines.\n");
        foreach (ApiStruct s in structs)
        {
            text.Append("; STRUCT ").Append(s.Canonical).Append('\n');
            foreach (ApiStructField field in s.Fields)
            {
                text.Append("%assign ").Append(s.Name).Append('.').Append(field.Name).Append(' ').Append(field.Offset.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            text.Append("%assign ").Append(s.Name).Append("_size ").Append(s.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The same constants for llvm-mc: <c>.set rect.min, 0</c>. GNU assembly has no preprocessor
    /// numbers, so these are absolute symbols; llvm-mc keeps them as local symbols of the object
    /// (measured on ELF, COFF and Mach-O), as it keeps the defines.
    /// </summary>
    /// <param name="structs">The structs, in declaration order.</param>
    /// <returns>The file's text.</returns>
    internal static string ToGnu(IEnumerable<ApiStruct> structs)
    {
        var text = new StringBuilder();
        text.Append("// Generated by AsmFoundation.Sdk from the STRUCT lines of this source and its includes. Do not edit; edit the STRUCT lines.\n");
        foreach (ApiStruct s in structs)
        {
            text.Append("// STRUCT ").Append(s.Canonical).Append('\n');
            foreach (ApiStructField field in s.Fields)
            {
                text.Append(".set ").Append(s.Name).Append('.').Append(field.Name).Append(", ").Append(field.Offset.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            text.Append(".set ").Append(s.Name).Append("_size, ").Append(s.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>A JSON number.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The literal.</returns>
    private static JsonLiteral Number(int value) => new(value.ToString(CultureInfo.InvariantCulture));
}
