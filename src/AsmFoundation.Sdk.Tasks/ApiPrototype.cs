using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AsmFoundation.Sdk.Tasks;

/// <summary>
/// A C type as a PROTO line spells it: a base type with pointers, or a function pointer.
/// </summary>
/// <remarks>
/// Only what crosses the C ABI identically on every RID the SDK builds: the fixed-width
/// integers (C's <c>int</c> and <c>long</c> are refused, because <c>long</c> is 32-bit on
/// Windows and 64-bit elsewhere), <c>float</c>, <c>double</c>, <c>bool</c>, <c>char</c>,
/// <c>void</c>, pointers to any of them, and function pointers. <c>const</c> is kept only where
/// it changes a binding (on the base of a pointer: <c>const uint8_t*</c> reads,
/// <c>uint8_t*</c> writes); a const on the pointer itself is dropped from the canonical form.
/// </remarks>
internal sealed class ApiType
{
    /// <summary>The integer base types, which pass in integer registers (as pointers do).</summary>
    internal static readonly HashSet<string> IntegerBases = new(StringComparer.Ordinal)
    {
        "bool", "char", "int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t",
        "int64_t", "uint64_t", "size_t", "ptrdiff_t", "intptr_t", "uintptr_t",
    };

    /// <summary>Every base type a PROTO line may name.</summary>
    internal static readonly HashSet<string> KnownBases = new(IntegerBases.Concat(new[] { "float", "double", "void" }), StringComparer.Ordinal);

    /// <summary>Creates a base type with pointers.</summary>
    /// <param name="baseType">The base type's name.</param>
    /// <param name="isConst">Whether the base is const (<c>const uint8_t*</c>).</param>
    /// <param name="pointers">How many pointer levels follow the base.</param>
    internal ApiType(string baseType, bool isConst, int pointers)
    {
        Base = baseType;
        Const = isConst;
        Pointers = pointers;
    }

    /// <summary>Creates a function pointer type.</summary>
    /// <param name="callback">The function's signature.</param>
    internal ApiType(ApiCallback callback)
    {
        Base = "";
        Callback = callback;
        Pointers = 1;
    }

    /// <summary>The base type's name; empty for a function pointer.</summary>
    internal string Base { get; }

    /// <summary>Whether the base is const.</summary>
    internal bool Const { get; }

    /// <summary>The pointer levels after the base (1 for a function pointer).</summary>
    internal int Pointers { get; }

    /// <summary>The signature, when this is a function pointer.</summary>
    internal ApiCallback? Callback { get; }

    /// <summary>
    /// Whether a value of this type passes in an integer register: integers, pointers and
    /// function pointers do, float and double do not. FUNCTION's argument count counts these
    /// (asmsdk.inc: "the number of integer arguments").
    /// </summary>
    internal bool IsIntegerArgument => Callback is not null || Pointers > 0 || IntegerBases.Contains(Base);

    /// <summary>The canonical C spelling of a declaration of this type.</summary>
    /// <param name="declarator">The declared name, or null for none.</param>
    /// <returns>For example "const uint8_t* data", or "int32_t (*cmp)(int32_t, int32_t)".</returns>
    internal string ToC(string? declarator)
    {
        if (Callback is not null)
        {
            return $"{Callback.Returns.ToC(null)} (*{declarator})({ApiPrototype.ParameterList(Callback.Parameters)})";
        }

        string type = (Const ? "const " : "") + Base + new string('*', Pointers);
        return declarator is null ? type : type + " " + declarator;
    }

    /// <summary>The type as the API file writes it.</summary>
    /// <returns>{"base", "const", "pointers"}, or {"callback": {"returns", "parameters"}}.</returns>
    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Callback is not null)
        {
            var callback = new JsonObject();
            callback["returns"] = Callback.Returns.ToJson();
            callback["parameters"] = ApiPrototype.ParametersToJson(Callback.Parameters);
            json["callback"] = callback;
            return json;
        }

        json["base"] = new JsonString(Base);
        json["const"] = new JsonLiteral(Const ? "true" : "false");
        json["pointers"] = new JsonLiteral(Pointers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return json;
    }
}

/// <summary>A function pointer's signature.</summary>
/// <param name="returns">The return type.</param>
/// <param name="parameters">The parameters, in order.</param>
internal sealed class ApiCallback(ApiType returns, IReadOnlyList<ApiParameter> parameters)
{
    /// <summary>The return type.</summary>
    internal ApiType Returns { get; } = returns;

    /// <summary>The parameters, in order.</summary>
    internal IReadOnlyList<ApiParameter> Parameters { get; } = parameters;
}

/// <summary>A parameter: its type, and its name when the prototype gives one.</summary>
/// <param name="name">The name, or null.</param>
/// <param name="type">The type.</param>
internal sealed class ApiParameter(string? name, ApiType type)
{
    /// <summary>The name, or null for an unnamed parameter.</summary>
    internal string? Name { get; } = name;

    /// <summary>The type.</summary>
    internal ApiType Type { get; } = type;
}

/// <summary>
/// A PROTO line, parsed: <c>[fast] return-type name(parameters)</c> in C syntax, restricted to
/// the types of <see cref="ApiType"/>.
/// </summary>
/// <remarks>
/// <para>
/// The SDK reads the line from each object's API records (see <see cref="AsmReadApi"/>), so
/// it is parsed exactly as the assembler recorded it, after its preprocessor: per-RID source
/// variants and conditional assembly give each RID its own lines, which
/// <see cref="AsmMergeApi"/> then compares through <see cref="Canonical"/>.
/// </para>
/// <para>
/// Refused, each with a message that names the fix (ASM0130): C's <c>int</c>, <c>long</c>,
/// <c>short</c>, <c>unsigned</c> and <c>signed</c>; unknown type names; variadic functions;
/// arrays; a <c>void</c> parameter beside others; two parameters of one name.
/// </para>
/// </remarks>
internal sealed class ApiPrototype
{
    /// <summary>The C keywords whose size or signedness differs between the SDK's platforms.</summary>
    private static readonly HashSet<string> PlatformDependentTypes = new(StringComparer.Ordinal) { "int", "long", "short", "unsigned", "signed" };

    /// <summary>The tokens of the line being parsed.</summary>
    private readonly List<string> _tokens;

    /// <summary>The next token's index.</summary>
    private int _position;

    /// <summary>Creates a parser over the line's tokens.</summary>
    /// <param name="tokens">The tokens.</param>
    private ApiPrototype(List<string> tokens) => _tokens = tokens;

    /// <summary>Whether the line says "fast": the binding skips the GC transition.</summary>
    internal bool Fast { get; private set; }

    /// <summary>The return type.</summary>
    internal ApiType Returns { get; private set; } = new("void", false, 0);

    /// <summary>The function's name, the symbol the binding calls.</summary>
    internal string Name { get; private set; } = "";

    /// <summary>The parameters, in order (empty for "()" and "(void)").</summary>
    internal IReadOnlyList<ApiParameter> Parameters { get; private set; } = Array.Empty<ApiParameter>();

    /// <summary>
    /// The prototype in one spelling (single spaces, "const T*", "(void)" for none): two lines
    /// that declare the same function the same way have the same canonical form, whatever their
    /// spacing. Parameter names are part of it, so the generated C# is the same for every RID.
    /// </summary>
    internal string Canonical => (Fast ? "fast " : "") + Returns.ToC(null) + " " + Name + "(" + ParameterList(Parameters) + ")";

    /// <summary>The number of parameters that pass in integer registers: what FUNCTION's count states.</summary>
    internal int IntegerArgumentCount => Parameters.Count(p => p.Type.IsIntegerArgument);

    /// <summary>Parses a PROTO line (the text after "PROTO").</summary>
    /// <param name="text">The line, as recorded.</param>
    /// <returns>The prototype.</returns>
    /// <exception cref="FormatException">The line is not a prototype this SDK binds; the message says what to write instead.</exception>
    internal static ApiPrototype Parse(string text)
    {
        var parser = new ApiPrototype(Tokenize(text));
        if (parser.Peek() == "fast")
        {
            parser._position++;
            parser.Fast = true;
        }

        parser.Returns = parser.ParseType(allowVoid: true);
        parser.Name = parser.ExpectIdentifier("the function's name");
        parser.Expect("(");
        parser.Parameters = parser.ParseParameters();
        if (parser._position != parser._tokens.Count)
        {
            throw new FormatException($"unexpected '{parser._tokens[parser._position]}' after the parameter list");
        }

        return parser;
    }

    /// <summary>The canonical spelling of a parameter list: "void" when empty.</summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The list, without its parentheses.</returns>
    internal static string ParameterList(IReadOnlyList<ApiParameter> parameters) =>
        parameters.Count == 0 ? "void" : string.Join(", ", parameters.Select(p => p.Type.ToC(p.Name)));

    /// <summary>Parameters as the API file writes them.</summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>An array of {"name" (when given), "type"}.</returns>
    internal static JsonArray ParametersToJson(IReadOnlyList<ApiParameter> parameters)
    {
        var array = new JsonArray();
        foreach (ApiParameter parameter in parameters)
        {
            var json = new JsonObject();
            if (parameter.Name is not null)
            {
                json["name"] = new JsonString(parameter.Name);
            }

            json["type"] = parameter.Type.ToJson();
            array.Items.Add(json);
        }

        return array;
    }

    /// <summary>Splits a line into identifiers and the punctuation a prototype uses.</summary>
    /// <param name="text">The line.</param>
    /// <returns>The tokens.</returns>
    /// <exception cref="FormatException">A character no prototype has (arrays, variadic dots, digits, ...).</exception>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '_' || char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && (text[i] == '_' || char.IsLetterOrDigit(text[i])))
                {
                    i++;
                }

                tokens.Add(text.Substring(start, i - start));
            }
            else if (c is '(' or ')' or ',' or '*')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '.' && text.IndexOf("...", i, StringComparison.Ordinal) == i)
            {
                throw new FormatException("a variadic function (...) cannot be bound: its arguments have no types to generate");
            }
            else if (c is '[' or ']')
            {
                throw new FormatException("an array parameter cannot be bound: write a pointer and a length (const uint8_t* data, size_t len)");
            }
            else
            {
                throw new FormatException($"unexpected character '{c}'");
            }
        }

        return tokens;
    }

    /// <summary>Parses "(" already consumed, through the closing ")".</summary>
    /// <returns>The parameters.</returns>
    /// <exception cref="FormatException">A malformed or refused parameter.</exception>
    private IReadOnlyList<ApiParameter> ParseParameters()
    {
        var parameters = new List<ApiParameter>();
        // "()" and "(void)" both mean no parameters.
        if (Peek() == ")")
        {
            _position++;
            return parameters;
        }

        if (Peek() == "void" && Peek(1) == ")")
        {
            _position += 2;
            return parameters;
        }

        while (true)
        {
            ApiType type = ParseType(allowVoid: false);
            if (Peek() == "(")
            {
                // A function pointer: R (*name)(params). The return type is the one just read.
                _position++;
                Expect("*");
                string? callbackName = Peek() == ")" ? null : ExpectIdentifier("the function pointer's name");
                Expect(")");
                Expect("(");
                IReadOnlyList<ApiParameter> callbackParameters = ParseParameters();
                parameters.Add(new ApiParameter(callbackName, new ApiType(new ApiCallback(type, callbackParameters))));
            }
            else
            {
                string? name = IsIdentifier(Peek()) ? Next() : null;
                parameters.Add(new ApiParameter(name, type));
            }

            string? separator = Next();
            if (separator == ")")
            {
                break;
            }

            if (separator != ",")
            {
                throw new FormatException(separator is null ? "the parameter list is not closed: ')' is missing" : $"expected ',' or ')' but found '{separator}'");
            }
        }

        string? duplicate = parameters.Where(p => p.Name is not null).GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new FormatException($"two parameters are named '{duplicate}'");
        }

        return parameters;
    }

    /// <summary>Parses a type: const, a base type, const again, pointers (each optionally const).</summary>
    /// <param name="allowVoid">Whether plain void is allowed (a return type) or only void* (a parameter).</param>
    /// <returns>The type.</returns>
    /// <exception cref="FormatException">A platform-dependent or unknown base type, or a void parameter.</exception>
    private ApiType ParseType(bool allowVoid)
    {
        bool isConst = false;
        while (Peek() == "const")
        {
            _position++;
            isConst = true;
        }

        string? baseType = Next();
        if (baseType is null || !IsIdentifier(baseType))
        {
            throw new FormatException(baseType is null ? "a type is missing at the end of the line" : $"expected a type but found '{baseType}'");
        }

        if (PlatformDependentTypes.Contains(baseType))
        {
            throw new FormatException($"'{baseType}' differs between Windows and the other platforms (long is 32-bit on Windows, 64-bit elsewhere): write a fixed-width type (int32_t, uint32_t, int64_t, uint64_t, ...)");
        }

        if (!ApiType.KnownBases.Contains(baseType))
        {
            throw new FormatException($"unknown type '{baseType}': PROTO takes void, bool, char, the fixed-width integers (int8_t ... uint64_t, size_t, ptrdiff_t, intptr_t, uintptr_t), float and double, pointers to them, and function pointers");
        }

        while (Peek() == "const")
        {
            _position++;
            isConst = true;
        }

        int pointers = 0;
        while (Peek() == "*")
        {
            _position++;
            pointers++;
            // A const pointer (uint8_t* const p) passes the same way: dropped.
            while (Peek() == "const")
            {
                _position++;
            }
        }

        if (baseType == "void" && pointers == 0 && !allowVoid)
        {
            throw new FormatException("a void parameter beside others: write (void) alone for no parameters, or void* for a pointer");
        }

        // const on a value (const int32_t x) passes the same way as without: kept only on a
        // pointer's base, where it says the function reads through the pointer.
        return new ApiType(baseType, isConst && pointers > 0, pointers);
    }

    /// <summary>Consumes an identifier.</summary>
    /// <param name="what">What it names, for the message.</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="FormatException">Something else is next.</exception>
    private string ExpectIdentifier(string what)
    {
        string? token = Next();
        if (token is null || !IsIdentifier(token))
        {
            throw new FormatException($"expected {what} but found {(token is null ? "the end of the line" : $"'{token}'")}");
        }

        return token;
    }

    /// <summary>Consumes a punctuation token.</summary>
    /// <param name="expected">The token.</param>
    /// <exception cref="FormatException">Something else is next.</exception>
    private void Expect(string expected)
    {
        string? token = Next();
        if (token != expected)
        {
            throw new FormatException($"expected '{expected}' but found {(token is null ? "the end of the line" : $"'{token}'")}");
        }
    }

    /// <summary>The token <paramref name="ahead"/> places after the current one, or null past the end.</summary>
    /// <param name="ahead">0 for the current token.</param>
    /// <returns>The token, or null.</returns>
    private string? Peek(int ahead = 0) => _position + ahead < _tokens.Count ? _tokens[_position + ahead] : null;

    /// <summary>Consumes and returns the current token, or null past the end.</summary>
    /// <returns>The token, or null.</returns>
    private string? Next() => _position < _tokens.Count ? _tokens[_position++] : null;

    /// <summary>Whether a token is an identifier (not punctuation, not a keyword this grammar reserves).</summary>
    /// <param name="token">The token.</param>
    /// <returns>True for an identifier.</returns>
    private static bool IsIdentifier(string? token) =>
        token is not null && token.Length > 0 && (token[0] == '_' || char.IsLetter(token[0])) && token != "const";
}
