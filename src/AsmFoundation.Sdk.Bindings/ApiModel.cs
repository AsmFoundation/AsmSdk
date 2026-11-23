using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AsmFoundation.Sdk.Tasks;

namespace AsmFoundation.Sdk.Bindings;

/// <summary>
/// An immutable array compared by its elements: the incremental generator compares each step's
/// output with the previous run's to skip the steps after it, and arrays compare by reference.
/// </summary>
/// <typeparam name="T">The element type, itself compared by value.</typeparam>
/// <param name="items">The elements (not copied: the caller hands over the array).</param>
internal readonly struct EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    /// <summary>The elements; empty for a default instance.</summary>
    private readonly T[] _items = items ?? Array.Empty<T>();

    /// <summary>The number of elements.</summary>
    public int Count => (_items ?? Array.Empty<T>()).Length;

    /// <summary>The element at an index.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The element.</returns>
    public T this[int index] => (_items ?? Array.Empty<T>())[index];

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other) => (_items ?? Array.Empty<T>()).SequenceEqual(other._items ?? Array.Empty<T>());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in _items ?? Array.Empty<T>())
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }

        return hash;
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A C type from the API file: a base with pointers, or a function pointer.</summary>
/// <param name="Base">The base type's C name; empty for a function pointer.</param>
/// <param name="Const">Whether a pointer's base is const (the function only reads through it).</param>
/// <param name="Pointers">The pointer levels.</param>
/// <param name="Callback">The signature of a function pointer, or null.</param>
internal sealed record ApiTypeModel(string Base, bool Const, int Pointers, ApiCallbackModel? Callback);

/// <summary>A function pointer's signature.</summary>
/// <param name="Returns">The return type.</param>
/// <param name="Parameters">The parameters.</param>
internal sealed record ApiCallbackModel(ApiTypeModel Returns, EquatableArray<ApiParameterModel> Parameters);

/// <summary>A parameter, named or not.</summary>
/// <param name="Name">The C name, or null.</param>
/// <param name="Type">The type.</param>
internal sealed record ApiParameterModel(string? Name, ApiTypeModel Type);

/// <summary>One PROTO line of the library.</summary>
/// <param name="Name">The symbol.</param>
/// <param name="File">The logical file (the nesting unit): "simd/sum".</param>
/// <param name="Fast">Whether the binding skips the GC transition.</param>
/// <param name="Prototype">The canonical C prototype, for the XML summary.</param>
/// <param name="Returns">The return type.</param>
/// <param name="Parameters">The parameters.</param>
internal sealed record ApiFunctionModel(string Name, string File, bool Fast, string Prototype, ApiTypeModel Returns, EquatableArray<ApiParameterModel> Parameters);

/// <summary>A library's API file, read: its import name and its functions.</summary>
/// <param name="Library">The import name (the library name of the generated [DllImport]s).</param>
/// <param name="Functions">The functions, sorted by name.</param>
/// <param name="Path">The file it was read from, for messages.</param>
internal sealed record ApiLibraryModel(string Library, EquatableArray<ApiFunctionModel> Functions, string Path)
{
    /// <summary>Reads an API file (format 1, as AsmReadApi and AsmMergeApi write it).</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="path">The file, for messages.</param>
    /// <returns>The library.</returns>
    /// <exception cref="FormatException">Not JSON, or not an API file of format 1.</exception>
    internal static ApiLibraryModel Parse(string json, string path)
    {
        try
        {
            var root = (JsonObject)Json.Parse(json);
            string format = ((JsonLiteral)root["format"]!).Text;
            if (format != "1")
            {
                throw new FormatException($"format {format}, which this SDK's generator does not read (it reads 1)");
            }

            var functions = ((JsonArray)root["functions"]!).Items.Select(node =>
            {
                var function = (JsonObject)node;
                return new ApiFunctionModel(
                    Text(function, "name"),
                    Text(function, "file"),
                    ((JsonLiteral)function["fast"]!).Text == "true",
                    Text(function, "prototype"),
                    ReadType((JsonObject)function["returns"]!),
                    ReadParameters((JsonArray)function["parameters"]!));
            }).ToArray();
            return new ApiLibraryModel(Text(root, "library"), new EquatableArray<ApiFunctionModel>(functions), path);
        }
        catch (Exception ex) when (ex is InvalidCastException or NullReferenceException)
        {
            throw new FormatException("a member is missing or of the wrong kind", ex);
        }
    }

    /// <summary>A string member.</summary>
    /// <param name="json">The object.</param>
    /// <param name="name">The member.</param>
    /// <returns>Its value.</returns>
    private static string Text(JsonObject json, string name) => ((JsonString)json[name]!).Value;

    /// <summary>A type object: {"base", "const", "pointers"} or {"callback"}.</summary>
    /// <param name="json">The object.</param>
    /// <returns>The type.</returns>
    private static ApiTypeModel ReadType(JsonObject json)
    {
        if (json["callback"] is JsonObject callback)
        {
            return new ApiTypeModel("", false, 1, new ApiCallbackModel(ReadType((JsonObject)callback["returns"]!), ReadParameters((JsonArray)callback["parameters"]!)));
        }

        return new ApiTypeModel(
            Text(json, "base"),
            ((JsonLiteral)json["const"]!).Text == "true",
            int.Parse(((JsonLiteral)json["pointers"]!).Text, System.Globalization.CultureInfo.InvariantCulture),
            null);
    }

    /// <summary>A parameter array.</summary>
    /// <param name="json">The array.</param>
    /// <returns>The parameters.</returns>
    private static EquatableArray<ApiParameterModel> ReadParameters(JsonArray json) =>
        new(json.Items.Select(node =>
        {
            var parameter = (JsonObject)node;
            return new ApiParameterModel(parameter["name"] is JsonString name ? name.Value : null, ReadType((JsonObject)parameter["type"]!));
        }).ToArray());
}
