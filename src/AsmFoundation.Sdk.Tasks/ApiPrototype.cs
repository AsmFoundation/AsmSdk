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
    /// <param name="baseType">The base type's name: a C type, or a STRUCT's name when <paramref name="isStruct"/>.</param>
    /// <param name="isConst">Whether the base is const (<c>const uint8_t*</c>).</param>
    /// <param name="pointers">How many pointer levels follow the base.</param>
    /// <param name="isStruct">
    /// Whether the base names a STRUCT of the library rather than a C type. Recorded so that the
    /// API file says so: the generator maps a C type to a C# primitive and a struct to the struct
    /// it generates, and must never guess which one a name is.
    /// </param>
    internal ApiType(string baseType, bool isConst, int pointers, bool isStruct = false)
    {
        Base = baseType;
        Const = isConst;
        Pointers = pointers;
        IsStruct = isStruct;
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

    /// <summary>Whether <see cref="Base"/> names one of the library's STRUCTs (always false for a function pointer).</summary>
    internal bool IsStruct { get; }

    /// <summary>
    /// Whether a value of this type passes in an integer register: integers, pointers and
    /// function pointers do, float and double do not. FUNCTION's argument count counts these
    /// (asmsdk.inc: "the number of integer arguments").
    /// </summary>
    internal bool IsIntegerArgument => Callback is not null || Pointers > 0 || IntegerBases.Contains(Base);

    /// <summary>
    /// The WebAssembly value type a value of this type passes as on wasm32, as clang lowers C:
    /// <c>i32</c> for pointers, function pointers (a table index), every integer of 32 bits or
    /// fewer and the pointer-sized ones (<c>size_t</c>, <c>intptr_t</c> and kin are 32-bit on
    /// wasm32); <c>i64</c> for <c>int64_t</c> and <c>uint64_t</c>; <c>f32</c> for <c>float</c>;
    /// <c>f64</c> for <c>double</c>; null for <c>void</c>, which is no value at all.
    /// </summary>
    /// <remarks>
    /// A STRUCT by value has no single value type, and never reaches here: a PROTO line may only
    /// point at one (ASM0130), and a pointer is <c>i32</c> like any other. <c>bool</c> and
    /// <c>char</c> widen to <c>i32</c>, as every integer below 32 bits does: WebAssembly has no
    /// narrower value type.
    /// </remarks>
    internal string? WasmValueType => Callback is not null || Pointers > 0
        ? "i32"
        : Base switch
        {
            "void" => null,
            "int64_t" or "uint64_t" => "i64",
            "float" => "f32",
            "double" => "f64",
            _ => "i32",
        };

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
    /// <returns>{"base", "const", "pointers"} (plus "struct": true for a STRUCT's name), or {"callback": {"returns", "parameters"}}.</returns>
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
        // Written only when true, so the API file of a library without STRUCTs is byte for byte
        // what phase 2 wrote.
        if (IsStruct)
        {
            json["struct"] = new JsonLiteral("true");
        }

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
/// A STRUCT field as written: its name, its type, and its element count when it is an array
/// (<c>uint32_t h[8]</c>). Syntax only: what the field may hold and where it lands is
/// <see cref="ApiStruct"/>'s to decide.
/// </summary>
/// <param name="name">The field's name.</param>
/// <param name="type">Its type (the element type of an array).</param>
/// <param name="count">The array's element count, or null for a single value.</param>
internal sealed class ApiFieldDeclaration(string name, ApiType type, int? count)
{
    /// <summary>The field's name.</summary>
    internal string Name { get; } = name;

    /// <summary>The field's type (an array's element type).</summary>
    internal ApiType Type { get; } = type;

    /// <summary>The array's element count, or null.</summary>
    internal int? Count { get; } = count;
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
/// arrays; a <c>void</c> parameter beside others; two parameters of one name; a STRUCT passed
/// or returned by value (each ABI passes one its own way: PROTO takes a pointer to it).
/// </para>
/// <para>
/// The same parser reads STRUCT lines (<see cref="ParseStruct"/>): a struct's fields are C
/// declarations of the same types, plus arrays and STRUCTs by value, so both lines share one
/// grammar, one canonical spelling and one set of messages.
/// </para>
/// </remarks>
internal sealed class ApiPrototype
{
    /// <summary>The C keywords whose size or signedness differs between the SDK's platforms.</summary>
    private static readonly HashSet<string> PlatformDependentTypes = new(StringComparer.Ordinal) { "int", "long", "short", "unsigned", "signed" };

    /// <summary>The largest array a STRUCT field may declare: far beyond any register-sized struct, and far below an int's overflow when sizes are multiplied.</summary>
    private const int MaxArrayLength = 1 << 20;

    /// <summary>The tokens of the line being parsed.</summary>
    private readonly List<string> _tokens;

    /// <summary>Whether a name is one of the library's STRUCTs (declared before this line); null when none are known.</summary>
    private readonly Func<string, bool>? _isStruct;

    /// <summary>The next token's index.</summary>
    private int _position;

    /// <summary>Creates a parser over the line's tokens.</summary>
    /// <param name="tokens">The tokens.</param>
    /// <param name="isStruct">Says which names are STRUCTs, or null when the line may name none.</param>
    private ApiPrototype(List<string> tokens, Func<string, bool>? isStruct)
    {
        _tokens = tokens;
        _isStruct = isStruct;
    }

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

    /// <summary>
    /// The prototype lowered to a WebAssembly function type, in the spelling of asmsdk-wasm.inc's
    /// FUNCTION: <c>(i32, i32) -&gt; (i32)</c>, <c>() -&gt; ()</c> for <c>void f(void)</c>. On wasm the
    /// FUNCTION macro records its exact type, so the PROTO line is compared with all of it, where
    /// native code can compare only the integer-argument count.
    /// </summary>
    internal string WasmSignature
    {
        get
        {
            string parameters = string.Join(", ", Parameters.Select(p => p.Type.WasmValueType));
            return $"({parameters}) -> ({Returns.WasmValueType})";
        }
    }

    /// <summary>
    /// A WebAssembly function type with its spacing removed, so that two spellings of one type
    /// compare equal: <c>(i32,i32)-&gt;(i32)</c> for <c>(i32, i32) -&gt; (i32)</c> and
    /// <c>(i32,i32)  -&gt;  (i32)</c> alike.
    /// </summary>
    /// <param name="signature">A function type as written after FUNCTION's name, or as <see cref="WasmSignature"/> spells it.</param>
    /// <returns>The type without whitespace.</returns>
    internal static string NormalizeWasmSignature(string signature) =>
        new string(signature.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    /// <summary>Parses a PROTO line (the text after "PROTO").</summary>
    /// <param name="text">The line, as recorded.</param>
    /// <param name="isStruct">
    /// Which names are the library's STRUCTs (the RID's struct records), or null for none. A
    /// STRUCT may then appear behind a pointer (<c>const rect* r</c>), never by value.
    /// </param>
    /// <returns>The prototype.</returns>
    /// <exception cref="FormatException">The line is not a prototype this SDK binds; the message says what to write instead.</exception>
    internal static ApiPrototype Parse(string text, Func<string, bool>? isStruct = null)
    {
        var parser = new ApiPrototype(Tokenize(text, arrays: false), isStruct);
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

    /// <summary>
    /// Parses a STRUCT line (the text after "STRUCT"): the struct's name, then its fields, each a
    /// C declaration: <c>uint32_t crc</c>, <c>const uint8_t* data</c>, <c>uint32_t h[8]</c>,
    /// <c>point min</c> (a STRUCT by value), <c>int32_t (*cmp)(int32_t, int32_t)</c>.
    /// </summary>
    /// <param name="text">The line, as written or recorded.</param>
    /// <param name="isStruct">
    /// Which names are STRUCTs declared before this line. The struct's own name counts too, so
    /// that it may point at itself (<c>node* next</c>); holding itself by value is refused by
    /// <see cref="ApiStruct"/>, which knows the sizes.
    /// </param>
    /// <returns>The struct's name and its fields, in order.</returns>
    /// <exception cref="FormatException">
    /// The line is not a struct this SDK lays out: no field, a malformed declaration, an unknown
    /// or platform-dependent type, a void field, a multi-dimensional or empty array, or two fields
    /// of one name. The message says what to write instead.
    /// </exception>
    /// <remarks>
    /// Fields are separated by the commas outside parentheses, so a function pointer's parameter
    /// list keeps its own commas: the tokens are read in order, never split on commas first.
    /// </remarks>
    internal static (string Name, IReadOnlyList<ApiFieldDeclaration> Fields) ParseStruct(string text, Func<string, bool> isStruct)
    {
        List<string> tokens = Tokenize(text, arrays: true);
        string? first = tokens.Count > 0 ? tokens[0] : null;
        // The struct's own name is a struct name for its fields (a pointer to itself), so the
        // resolver is wrapped once the name is known.
        var parser = new ApiPrototype(tokens, name => name == first || isStruct(name));
        string structName = parser.ExpectIdentifier("the struct's name");
        if (ApiType.KnownBases.Contains(structName) || PlatformDependentTypes.Contains(structName))
        {
            throw new FormatException($"'{structName}' is a C type: name the struct something else");
        }

        if (parser.Peek() is null)
        {
            throw new FormatException($"{structName} has no field: a struct needs at least one (C has no empty structs, and C# would give it a size C does not)");
        }

        parser.Expect(",");
        var fields = new List<ApiFieldDeclaration>();
        while (true)
        {
            ApiType type = parser.ParseType(allowVoid: true, allowStructValue: true);
            string fieldName;
            int? count = null;
            if (parser.Peek() == "(")
            {
                // A function pointer field: R (*name)(params), the return type just read. It was
                // read as a field's type, where a STRUCT by value is a nested struct; as a
                // callback's result it is a value passed by the ABI, refused as in PROTO.
                if (type.IsStruct && type.Pointers == 0)
                {
                    throw ByValue(type.Base);
                }

                parser._position++;
                parser.Expect("*");
                fieldName = parser.ExpectIdentifier("the function pointer field's name");
                parser.Expect(")");
                parser.Expect("(");
                type = new ApiType(new ApiCallback(type, parser.ParseParameters()));
            }
            else
            {
                if (type.Base == "void" && type.Pointers == 0)
                {
                    throw new FormatException("a void field holds nothing: write void* for a pointer");
                }

                fieldName = parser.ExpectIdentifier("the field's name");
            }

            if (parser.Peek() == "[")
            {
                parser._position++;
                count = ParseArrayLength(parser.Next());
                parser.Expect("]");
                if (parser.Peek() == "[")
                {
                    throw new FormatException($"{fieldName} has two dimensions: write one, the product of both ({fieldName}[rows * columns], indexed row * columns + column)");
                }
            }

            if (fields.Any(f => f.Name == fieldName))
            {
                throw new FormatException($"two fields are named '{fieldName}'");
            }

            fields.Add(new ApiFieldDeclaration(fieldName, type, count));
            string? separator = parser.Next();
            if (separator is null)
            {
                break;
            }

            if (separator != ",")
            {
                throw new FormatException($"expected ',' between two fields but found '{separator}'");
            }
        }

        return (structName, fields);
    }

    /// <summary>Reads an array's length: a decimal or 0x hexadecimal number from 1 to <see cref="MaxArrayLength"/>.</summary>
    /// <param name="token">The token between the brackets, or null at the end of the line.</param>
    /// <returns>The length.</returns>
    /// <exception cref="FormatException">Not such a number: a name (no constants from elsewhere), 0, or too large.</exception>
    private static int ParseArrayLength(string? token)
    {
        if (token is null || token.Length == 0 || !char.IsDigit(token[0]))
        {
            throw new FormatException(token is null || token == "]"
                ? "an array needs its length between the brackets"
                : $"an array's length must be a number written in the line, not '{token}'");
        }

        bool hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool parsed = hex
            ? long.TryParse(token.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out long length)
            : long.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length);
        if (!parsed || length < 1 || length > MaxArrayLength)
        {
            throw new FormatException($"an array's length must be from 1 to {MaxArrayLength}, not {token}");
        }

        return (int)length;
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

    /// <summary>The message for a STRUCT passed or returned by value, which no binding can make portable.</summary>
    /// <param name="name">The struct's name.</param>
    /// <returns>The exception, naming the pointer form to write instead.</returns>
    private static FormatException ByValue(string name) =>
        new($"{name} is a struct passed by value, which each platform passes its own way (Windows x64 by reference beyond 8 bytes, System V in registers or on the stack, ARM64 by reference beyond 16 bytes): take a pointer ({name}* or const {name}*)");

    /// <summary>Splits a line into identifiers and the punctuation a prototype uses.</summary>
    /// <param name="text">The line.</param>
    /// <param name="arrays">
    /// Whether brackets and numbers are tokens (a STRUCT line's arrays). A PROTO line refuses
    /// them with the pointer-and-length advice: an array parameter is a pointer in C anyway.
    /// </param>
    /// <returns>The tokens.</returns>
    /// <exception cref="FormatException">A character no prototype has (arrays, variadic dots, digits, ...).</exception>
    private static List<string> Tokenize(string text, bool arrays)
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
                if (!arrays)
                {
                    throw new FormatException("an array parameter cannot be bound: write a pointer and a length (const uint8_t* data, size_t len)");
                }

                tokens.Add(c.ToString());
                i++;
            }
            else if (arrays && char.IsDigit(c))
            {
                // An array's length: digits, and the letters of a 0x prefix and hex digits,
                // checked as a number by ParseArrayLength.
                int start = i;
                while (i < text.Length && char.IsLetterOrDigit(text[i]))
                {
                    i++;
                }

                tokens.Add(text.Substring(start, i - start));
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

    /// <summary>Parses a type: const, a base type (C's or a STRUCT's), const again, pointers (each optionally const).</summary>
    /// <param name="allowVoid">Whether plain void is allowed (a return type) or only void* (a parameter).</param>
    /// <param name="allowStructValue">
    /// Whether a STRUCT may stand without a pointer: a STRUCT field holding another struct by
    /// value. Never in PROTO or a callback's signature, where the ABI would pass it.
    /// </param>
    /// <returns>The type.</returns>
    /// <exception cref="FormatException">A platform-dependent or unknown base type, a void parameter, or a STRUCT by value where the ABI would pass it.</exception>
    private ApiType ParseType(bool allowVoid, bool allowStructValue = false)
    {
        bool isConst = false;
        while (Peek() == "const")
        {
            _position++;
            isConst = true;
        }

        // C's "struct rect* r": the keyword is accepted and dropped (a STRUCT's name is already a
        // type name, as a typedef'd struct's is), so a prototype copied from a C header parses.
        bool structKeyword = false;
        if (Peek() == "struct")
        {
            _position++;
            structKeyword = true;
        }

        string? baseType = Next();
        if (baseType is null || !IsIdentifier(baseType))
        {
            throw new FormatException(baseType is null ? "a type is missing at the end of the line" : $"expected a type but found '{baseType}'");
        }

        bool isStruct = _isStruct?.Invoke(baseType) == true;
        if (structKeyword && !isStruct)
        {
            throw new FormatException($"'struct {baseType}' names no STRUCT declared before this line: declare it first (STRUCT {baseType}, ...)");
        }

        if (!isStruct && PlatformDependentTypes.Contains(baseType))
        {
            throw new FormatException($"'{baseType}' differs between Windows and the other platforms (long is 32-bit on Windows, 64-bit elsewhere): write a fixed-width type (int32_t, uint32_t, int64_t, uint64_t, ...)");
        }

        if (!isStruct && !ApiType.KnownBases.Contains(baseType))
        {
            throw new FormatException($"unknown type '{baseType}': a PROTO or STRUCT line takes void, bool, char, the fixed-width integers (int8_t ... uint64_t, size_t, ptrdiff_t, intptr_t, uintptr_t), float, double, a STRUCT declared before the line, pointers to them, and function pointers");
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

        if (isStruct && pointers == 0 && !allowStructValue)
        {
            throw ByValue(baseType);
        }

        // const on a value (const int32_t x) passes the same way as without: kept only on a
        // pointer's base, where it says the function reads through the pointer.
        return new ApiType(baseType, isConst && pointers > 0, pointers, isStruct);
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

    /// <summary>Whether a token is an identifier (not punctuation or a number, not a keyword this grammar reserves).</summary>
    /// <param name="token">The token.</param>
    /// <returns>True for an identifier.</returns>
    private static bool IsIdentifier(string? token) =>
        token is not null && token.Length > 0 && (token[0] == '_' || char.IsLetter(token[0])) && token != "const" && token != "struct";
}
