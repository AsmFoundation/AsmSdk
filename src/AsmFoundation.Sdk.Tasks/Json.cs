using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// A JSON value, as read by <see cref="Json.Parse"/>: just enough of a document model to read
/// the .NET SDK's runtime identifier graph and to add entries to a deps.json file without
/// disturbing the rest of it.
/// </summary>
/// <remarks>
/// Why not System.Text.Json: this assembly targets netstandard2.0 so that both MSBuilds load it
/// (the .NET SDK's and Visual Studio's), and neither ships System.Text.Json at a version a
/// task may count on; shipping a copy would load a second one next to MSBuild's. The model
/// keeps object members in file order and numbers as their original text, so a file that is
/// read and written back changes only where it was edited (and in its indentation).
/// </remarks>
internal abstract class JsonNode
{
    /// <summary>Writes this value as indented JSON (two spaces, as the .NET SDK writes deps.json).</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="indent">The current depth.</param>
    internal abstract void Write(StringBuilder output, int indent);
}

/// <summary>A JSON object: members in file order, names compared ordinally (JSON is case-sensitive).</summary>
internal sealed class JsonObject : JsonNode
{
    /// <summary>The members, in order.</summary>
    private readonly List<KeyValuePair<string, JsonNode>> _members = new();

    /// <summary>The members, in order.</summary>
    internal IReadOnlyList<KeyValuePair<string, JsonNode>> Members => _members;

    /// <summary>
    /// Gets a member's value, or null when there is none; setting replaces the member in place
    /// (keeping its position) or appends it.
    /// </summary>
    /// <param name="name">The member name.</param>
    internal JsonNode? this[string name]
    {
        get
        {
            foreach (KeyValuePair<string, JsonNode> member in _members)
            {
                if (member.Key == name)
                {
                    return member.Value;
                }
            }

            return null;
        }
        set
        {
            if (value is null)
            {
                _members.RemoveAll(m => m.Key == name);
                return;
            }

            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].Key == name)
                {
                    _members[i] = new KeyValuePair<string, JsonNode>(name, value);
                    return;
                }
            }

            _members.Add(new KeyValuePair<string, JsonNode>(name, value));
        }
    }

    /// <summary>The member's value when it is an object; otherwise a new empty object, stored under that name.</summary>
    /// <param name="name">The member name.</param>
    /// <returns>The object, never null.</returns>
    /// <remarks>Footgun: a member of another type (an array, a string) is REPLACED by the new object.</remarks>
    internal JsonObject GetOrAddObject(string name)
    {
        if (this[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        this[name] = created;
        return created;
    }

    /// <inheritdoc />
    internal override void Write(StringBuilder output, int indent)
    {
        if (_members.Count == 0)
        {
            output.Append("{}");
            return;
        }

        output.Append("{\n");
        for (int i = 0; i < _members.Count; i++)
        {
            output.Append(' ', (indent + 1) * 2);
            JsonString.WriteEscaped(output, _members[i].Key);
            output.Append(": ");
            _members[i].Value.Write(output, indent + 1);
            output.Append(i + 1 < _members.Count ? ",\n" : "\n");
        }

        output.Append(' ', indent * 2).Append('}');
    }
}

/// <summary>A JSON array.</summary>
internal sealed class JsonArray : JsonNode
{
    /// <summary>The elements, in order.</summary>
    internal List<JsonNode> Items { get; } = new();

    /// <inheritdoc />
    internal override void Write(StringBuilder output, int indent)
    {
        if (Items.Count == 0)
        {
            output.Append("[]");
            return;
        }

        output.Append("[\n");
        for (int i = 0; i < Items.Count; i++)
        {
            output.Append(' ', (indent + 1) * 2);
            Items[i].Write(output, indent + 1);
            output.Append(i + 1 < Items.Count ? ",\n" : "\n");
        }

        output.Append(' ', indent * 2).Append(']');
    }
}

/// <summary>A JSON string, held unescaped.</summary>
/// <param name="value">The string's characters.</param>
internal sealed class JsonString(string value) : JsonNode
{
    /// <summary>The string's characters.</summary>
    internal string Value { get; } = value;

    /// <inheritdoc />
    internal override void Write(StringBuilder output, int indent) => WriteEscaped(output, Value);

    /// <summary>Writes a string with JSON's escapes: quote, backslash and control characters.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="value">The characters.</param>
    internal static void WriteEscaped(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}

/// <summary>A number, true, false or null, kept as its original text.</summary>
/// <param name="text">The token, exactly as it appeared.</param>
internal sealed class JsonLiteral(string text) : JsonNode
{
    /// <summary>The token's text.</summary>
    internal string Text { get; } = text;

    /// <inheritdoc />
    internal override void Write(StringBuilder output, int indent) => output.Append(Text);
}

/// <summary>Reads and writes <see cref="JsonNode"/> documents.</summary>
internal static class Json
{
    /// <summary>Parses a complete JSON document.</summary>
    /// <param name="text">The document (a leading byte-order mark is accepted).</param>
    /// <returns>The root value.</returns>
    /// <exception cref="FormatException">The text is not valid JSON (the message names the offset).</exception>
    internal static JsonNode Parse(string text)
    {
        int position = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        JsonNode root = ParseValue(text, ref position);
        SkipWhitespace(text, ref position);
        if (position != text.Length)
        {
            throw new FormatException($"unexpected text after the JSON value at offset {position}");
        }

        return root;
    }

    /// <summary>Writes a document as indented JSON, without a final newline (as the .NET SDK writes deps.json).</summary>
    /// <param name="root">The root value.</param>
    /// <returns>The text.</returns>
    internal static string Write(JsonNode root)
    {
        var output = new StringBuilder();
        root.Write(output, 0);
        return output.ToString();
    }

    /// <summary>Parses the value at <paramref name="position"/>.</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset; moved past the value.</param>
    /// <returns>The value.</returns>
    /// <exception cref="FormatException">The text there is not a JSON value.</exception>
    private static JsonNode ParseValue(string text, ref int position)
    {
        SkipWhitespace(text, ref position);
        if (position >= text.Length)
        {
            throw new FormatException("unexpected end of the JSON text");
        }

        char c = text[position];
        switch (c)
        {
            case '{':
                return ParseObject(text, ref position);
            case '[':
                return ParseArray(text, ref position);
            case '"':
                return new JsonString(ParseString(text, ref position));
            default:
                // Numbers and the three keywords: kept as text, so they are written back as read.
                int start = position;
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '-' or '+' or '.'))
                {
                    position++;
                }

                if (position == start)
                {
                    throw new FormatException($"unexpected character '{c}' at offset {position}");
                }

                string token = text.Substring(start, position - start);
                if (token is not ("true" or "false" or "null") && !(token[0] == '-' || char.IsDigit(token[0])))
                {
                    throw new FormatException($"unexpected token '{token}' at offset {start}");
                }

                return new JsonLiteral(token);
        }
    }

    /// <summary>Parses an object whose '{' is at <paramref name="position"/>.</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset; moved past the closing '}'.</param>
    /// <returns>The object.</returns>
    /// <exception cref="FormatException">A member or separator is malformed.</exception>
    private static JsonObject ParseObject(string text, ref int position)
    {
        var result = new JsonObject();
        position++;
        SkipWhitespace(text, ref position);
        if (Peek(text, position) == '}')
        {
            position++;
            return result;
        }

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (Peek(text, position) != '"')
            {
                throw new FormatException($"expected a member name at offset {position}");
            }

            string name = ParseString(text, ref position);
            SkipWhitespace(text, ref position);
            Expect(text, ref position, ':');
            result[name] = ParseValue(text, ref position);
            SkipWhitespace(text, ref position);
            char next = Peek(text, position);
            position++;
            if (next == '}')
            {
                return result;
            }

            if (next != ',')
            {
                throw new FormatException($"expected ',' or '}}' at offset {position - 1}");
            }
        }
    }

    /// <summary>Parses an array whose '[' is at <paramref name="position"/>.</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset; moved past the closing ']'.</param>
    /// <returns>The array.</returns>
    /// <exception cref="FormatException">An element or separator is malformed.</exception>
    private static JsonArray ParseArray(string text, ref int position)
    {
        var result = new JsonArray();
        position++;
        SkipWhitespace(text, ref position);
        if (Peek(text, position) == ']')
        {
            position++;
            return result;
        }

        while (true)
        {
            result.Items.Add(ParseValue(text, ref position));
            SkipWhitespace(text, ref position);
            char next = Peek(text, position);
            position++;
            if (next == ']')
            {
                return result;
            }

            if (next != ',')
            {
                throw new FormatException($"expected ',' or ']' at offset {position - 1}");
            }
        }
    }

    /// <summary>Parses a string whose opening quote is at <paramref name="position"/>, resolving its escapes.</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset; moved past the closing quote.</param>
    /// <returns>The string's characters.</returns>
    /// <exception cref="FormatException">The string is not closed, or an escape is malformed.</exception>
    private static string ParseString(string text, ref int position)
    {
        var value = new StringBuilder();
        position++;
        while (position < text.Length)
        {
            char c = text[position++];
            if (c == '"')
            {
                return value.ToString();
            }

            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            if (position >= text.Length)
            {
                break;
            }

            char escape = text[position++];
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    // Surrogate pairs arrive as two \u escapes; appending both halves keeps the pair.
                    if (position + 4 > text.Length || !int.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                    {
                        throw new FormatException($"malformed \\u escape at offset {position - 2}");
                    }

                    value.Append((char)code);
                    position += 4;
                    break;
                default:
                    throw new FormatException($"unknown escape '\\{escape}' at offset {position - 2}");
            }
        }

        throw new FormatException("unterminated string in the JSON text");
    }

    /// <summary>Moves past spaces, tabs and line breaks.</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset to advance.</param>
    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t' or '\n' or '\r')
        {
            position++;
        }
    }

    /// <summary>The character at an offset, or '\0' past the end (which no rule accepts).</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset.</param>
    /// <returns>The character.</returns>
    private static char Peek(string text, int position) => position < text.Length ? text[position] : '\0';

    /// <summary>Consumes one expected character.</summary>
    /// <param name="text">The document.</param>
    /// <param name="position">The offset; moved past the character.</param>
    /// <param name="expected">The character required there.</param>
    /// <exception cref="FormatException">Another character is there.</exception>
    private static void Expect(string text, ref int position, char expected)
    {
        if (Peek(text, position) != expected)
        {
            throw new FormatException($"expected '{expected}' at offset {position}");
        }

        position++;
    }
}
