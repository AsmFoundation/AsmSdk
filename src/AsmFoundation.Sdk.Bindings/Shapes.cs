using System;
using System.Collections.Generic;
using System.Linq;

namespace AsmFoundation.Sdk.Bindings;

/// <summary>
/// The shaping settings the project gives as MSBuild values: the AsmBindings* properties (an
/// environment variable of the same name arrives as one), or one library's AsmBindings item, whose
/// metadata the SDK's targets copy onto the library's API file. Kept as text: a value that is not
/// an option's is ASM0146 when it is used, not when it is read.
/// </summary>
/// <param name="Namespace">The baseline's namespace (an item only: one library's).</param>
/// <param name="Nesting">Files, Folders or Flat.</param>
/// <param name="SpanOverloads">true or false.</param>
/// <param name="Visibility">Internal, Public or ProtectedInternal.</param>
/// <param name="ClassKind">Static or Instance.</param>
/// <param name="Virtual">true or false.</param>
/// <param name="Sealed">true or false.</param>
/// <param name="DefaultBindings">true or false: whether the baseline's function classes are generated.</param>
/// <remarks>
/// Shared source: compiled into the generator (reading analyzer config options) and into the
/// SDK's tasks (reading task parameters), so both resolve a baseline the same way. No Roslyn type
/// may appear here.
/// </remarks>
internal sealed record ShapeSettings(string? Namespace, string? Nesting, string? SpanOverloads, string? Visibility, string? ClassKind, string? Virtual, string? Sealed, string? DefaultBindings)
{
    /// <summary>No settings at all.</summary>
    internal static readonly ShapeSettings None = new(null, null, null, null, null, null, null, null);

    /// <summary>AsmNesting's names, by value.</summary>
    internal static readonly string[] NestingNames = { "Files", "Folders", "Flat" };

    /// <summary>AsmVisibility's names, by value.</summary>
    internal static readonly string[] VisibilityNames = { "Internal", "Public", "ProtectedInternal" };

    /// <summary>AsmClassKind's names, by value.</summary>
    internal static readonly string[] ClassKindNames = { "Static", "Instance" };

    /// <summary>
    /// The project's AsmBindings* properties, read through a getter (the generator's global
    /// options under build_property.*, or the task's parameters).
    /// </summary>
    /// <param name="get">Returns a property's value by its MSBuild name, or null.</param>
    /// <returns>The settings.</returns>
    internal static ShapeSettings FromProperties(Func<string, string?> get) => new(
        null,
        Clean(get("AsmBindingsNesting")),
        Clean(get("AsmBindingsSpanOverloads")),
        Clean(get("AsmBindingsVisibility")),
        Clean(get("AsmBindingsClassKind")),
        Clean(get("AsmBindingsVirtual")),
        Clean(get("AsmBindingsSealed")),
        Clean(get("AsmDefaultBindings")));

    /// <summary>
    /// One library's AsmBindings item, read through a getter of its metadata names (Namespace,
    /// Nesting, SpanOverloads, Visibility, ClassKind, Virtual, Sealed, DefaultBindings).
    /// </summary>
    /// <param name="get">Returns a metadata value by name, or null.</param>
    /// <returns>The settings.</returns>
    internal static ShapeSettings FromItem(Func<string, string?> get) => new(
        Clean(get("Namespace")),
        Clean(get("Nesting")),
        Clean(get("SpanOverloads")),
        Clean(get("Visibility")),
        Clean(get("ClassKind")),
        Clean(get("Virtual")),
        Clean(get("Sealed")),
        Clean(get("DefaultBindings")));

    /// <summary>The metadata names of an item's settings, in the order of the record's members (for copying them onto an API file as AsmBindings&lt;Name&gt;).</summary>
    internal static readonly string[] ItemMetadataNames = { "Namespace", "Nesting", "SpanOverloads", "Visibility", "ClassKind", "Virtual", "Sealed", "DefaultBindings" };

    /// <summary>An enum setting's value, case-insensitive; null when empty; a problem when it is none of the names.</summary>
    /// <param name="text">The setting's text.</param>
    /// <param name="setting">Its name, for the message.</param>
    /// <param name="names">The enum's names, by value.</param>
    /// <param name="problems">Receives the problem.</param>
    /// <returns>The value, or null.</returns>
    internal static int? Enum(string? text, string setting, string[] names, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int index = Array.FindIndex(names, n => string.Equals(n, text!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            problems.Add($"{setting} is '{text}', which is not one of {string.Join(", ", names)}");
            return null;
        }

        return index;
    }

    /// <summary>A true/false setting; null when empty; a problem when it is neither.</summary>
    /// <param name="text">The setting's text.</param>
    /// <param name="setting">Its name, for the message.</param>
    /// <param name="problems">Receives the problem.</param>
    /// <returns>The value, or null.</returns>
    internal static bool? Bool(string? text, string setting, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (bool.TryParse(text!.Trim(), out bool value))
        {
            return value;
        }

        problems.Add($"{setting} is '{text}', which is not true or false");
        return null;
    }

    /// <summary>A value, or null when it is empty or white space.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The trimmed value, or null.</returns>
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
}

/// <summary>
/// The baseline options an [assembly: AsmLibrary] attribute wrote (its Default* options), null
/// where it wrote none: the code's level of the precedence.
/// </summary>
/// <param name="Namespace">DefaultNamespace.</param>
/// <param name="Nesting">DefaultNesting, as an int.</param>
/// <param name="SpanOverloads">DefaultSpanOverloads.</param>
/// <param name="Visibility">DefaultVisibility, as an int.</param>
/// <param name="ClassKind">DefaultClassKind, as an int.</param>
/// <param name="Virtual">DefaultVirtual.</param>
/// <param name="Sealed">DefaultSealed.</param>
/// <param name="Functions">DefaultBindings.</param>
internal sealed record BaselineOverrides(string? Namespace, int? Nesting, bool? SpanOverloads, int? Visibility, int? ClassKind, bool? Virtual, bool? Sealed, bool? Functions);

/// <summary>Resolving a library's baseline, and what C# cannot do with a shape: one rule set for the generator and the SDK's task.</summary>
internal static class Shapes
{
    /// <summary>
    /// A library's baseline options, the code first (the owner's choice of 2026-11-25): the
    /// per-library assembly attribute's Default* options, the global assembly attribute's, the
    /// project's AsmBindings item for the library, the AsmBindings* properties, then the defaults
    /// (the library's name as namespace, Files, span overloads, internal static classes, functions on).
    /// </summary>
    /// <param name="library">The library's import name.</param>
    /// <param name="perLibrary">The per-library attribute's options, or null (the SDK's task has none: attributes switch the generator on).</param>
    /// <param name="global">The global attribute's options (its Namespace is never used: one namespace cannot serve every library), or null.</param>
    /// <param name="item">The library's AsmBindings item.</param>
    /// <param name="properties">The AsmBindings* properties.</param>
    /// <param name="problems">Receives each ASM0146 message: a value no option has, options that do not fit together, a namespace C# cannot have.</param>
    /// <returns>The shape, or null when there are problems.</returns>
    internal static BaselineShape? Resolve(string library, BaselineOverrides? perLibrary, BaselineOverrides? global, ShapeSettings item, ShapeSettings properties, List<string> problems)
    {
        int before = problems.Count;
        string ns = perLibrary?.Namespace ?? item.Namespace ?? CSharpEmitter.LibraryNamespace(library);
        int nesting = perLibrary?.Nesting ?? global?.Nesting
            ?? ShapeSettings.Enum(item.Nesting, $"The AsmBindings item of {library}: Nesting", ShapeSettings.NestingNames, problems)
            ?? ShapeSettings.Enum(properties.Nesting, "AsmBindingsNesting", ShapeSettings.NestingNames, problems) ?? (int)Nesting.Files;
        bool spans = perLibrary?.SpanOverloads ?? global?.SpanOverloads
            ?? ShapeSettings.Bool(item.SpanOverloads, $"The AsmBindings item of {library}: SpanOverloads", problems)
            ?? ShapeSettings.Bool(properties.SpanOverloads, "AsmBindingsSpanOverloads", problems) ?? true;
        int visibility = perLibrary?.Visibility ?? global?.Visibility
            ?? ShapeSettings.Enum(item.Visibility, $"The AsmBindings item of {library}: Visibility", ShapeSettings.VisibilityNames, problems)
            ?? ShapeSettings.Enum(properties.Visibility, "AsmBindingsVisibility", ShapeSettings.VisibilityNames, problems) ?? 0;
        int classKind = perLibrary?.ClassKind ?? global?.ClassKind
            ?? ShapeSettings.Enum(item.ClassKind, $"The AsmBindings item of {library}: ClassKind", ShapeSettings.ClassKindNames, problems)
            ?? ShapeSettings.Enum(properties.ClassKind, "AsmBindingsClassKind", ShapeSettings.ClassKindNames, problems) ?? 0;
        bool isVirtual = perLibrary?.Virtual ?? global?.Virtual
            ?? ShapeSettings.Bool(item.Virtual, $"The AsmBindings item of {library}: Virtual", problems)
            ?? ShapeSettings.Bool(properties.Virtual, "AsmBindingsVirtual", problems) ?? false;
        bool isSealed = perLibrary?.Sealed ?? global?.Sealed
            ?? ShapeSettings.Bool(item.Sealed, $"The AsmBindings item of {library}: Sealed", problems)
            ?? ShapeSettings.Bool(properties.Sealed, "AsmBindingsSealed", problems) ?? false;
        bool functions = perLibrary?.Functions ?? global?.Functions
            ?? ShapeSettings.Bool(item.DefaultBindings, $"The AsmBindings item of {library}: DefaultBindings", problems)
            ?? ShapeSettings.Bool(properties.DefaultBindings, "AsmDefaultBindings", problems) ?? true;

        bool instance = classKind == 1;
        problems.AddRange(Problems(library + "'s baseline", instance, visibility, isVirtual, isSealed));
        if (ns.Split('.').Any(part => !IsIdentifier(part)))
        {
            problems.Add($"{library}'s baseline namespace '{ns}' is not a C# namespace name");
        }

        return problems.Count > before
            ? null
            : new BaselineShape(ns, (Nesting)nesting, spans, new MemberShape(MemberShape.AccessOf(visibility), !instance, isVirtual), isSealed, functions);
    }

    /// <summary>
    /// What C# cannot do with a shape: a protected member, a virtual member or a sealed modifier
    /// on a static class; virtual members in a sealed class (CS0549).
    /// </summary>
    /// <param name="what">What is being shaped, for the messages.</param>
    /// <param name="instance">Whether the members are instance members.</param>
    /// <param name="visibility">AsmVisibility as an int.</param>
    /// <param name="isVirtual">Whether the members are to be virtual.</param>
    /// <param name="isSealed">Whether the classes are to be sealed.</param>
    /// <returns>The problems, each a whole message.</returns>
    internal static IEnumerable<string> Problems(string what, bool instance, int visibility, bool isVirtual, bool isSealed)
    {
        if (!instance && visibility == 2)
        {
            yield return $"{what}: ProtectedInternal needs instance members (a static class holds no protected member, CS1057): set ClassKind to Instance";
        }

        if (!instance && isVirtual)
        {
            yield return $"{what}: Virtual needs instance members (a static member cannot be virtual): set ClassKind to Instance";
        }

        if (!instance && isSealed)
        {
            yield return $"{what}: Sealed needs instance classes (a static class is sealed already): set ClassKind to Instance, or leave Sealed out";
        }

        if (isVirtual && isSealed)
        {
            yield return $"{what}: Virtual and Sealed exclude each other (a sealed class's members cannot be overridden, CS0549)";
        }
    }

    /// <summary>Whether a namespace part is a C# identifier: a letter or underscore, then letters, digits and underscores (no keyword check: C# allows @-less namespace parts that are contextual keywords).</summary>
    /// <param name="part">The part.</param>
    /// <returns>True for an identifier.</returns>
    private static bool IsIdentifier(string part) =>
        part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_') && part.All(c => char.IsLetterOrDigit(c) || c == '_');
}
