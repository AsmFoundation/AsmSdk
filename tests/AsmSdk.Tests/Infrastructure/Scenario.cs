using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AsmSdk.Tests.Infrastructure;

/// <summary>
/// One scenario of tests/scenarios: a folder that holds a small repository (a .slnx with its
/// .csproj and .asmproj projects) and scenario.json, the commands to run on that repository, in
/// order, with what each must produce. ScenarioTests runs every scenario as one test, in a fresh
/// copy of the folder (see <see cref="ScenarioRunner"/>), so adding a scenario needs no C#.
/// </summary>
/// <remarks>
/// <para>
/// scenario.json is read strictly: an unknown property, a missing "description" or "run", an
/// unterminated quote or an unknown placeholder is an error naming the file and the reason, so
/// a typo fails the scenario instead of quietly checking less. Editors validate the format
/// against tests/scenarios/scenario.schema.json; docs/ecosystem/scenarios.md describes it.
/// </para>
/// <para>
/// The placeholders, in "run", "stdout" and "files": <c>{rid}</c> (this machine's RID),
/// <c>{exe:name}</c> (name.exe on Windows, name elsewhere), <c>{lib:name}</c> (this RID's shared
/// library file: name.dll, libname.dylib or libname.so) and <c>{port:service}</c> (the host port
/// a service container's port was published on).
/// </para>
/// </remarks>
internal sealed partial record Scenario
{
    /// <summary>The scenario file's name inside each scenario folder.</summary>
    public const string FileName = "scenario.json";

    /// <summary>
    /// How scenario.json is read: camelCase names, comments and trailing commas tolerated (the
    /// file is written by hand), and unknown properties refused (a misspelt expectation would
    /// otherwise be silently skipped, and the scenario would check less than it says).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The JSON schema reference that editors follow; not used by the runner.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>What the scenario proves, in a sentence or two: shown in the test's log.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The build machines (by their RID: win-x64, linux-arm64, ...) the scenario runs on; null
    /// for every machine AsmSdk builds for. Elsewhere the test is skipped with the reason.
    /// </summary>
    public IReadOnlyList<string>? Rids { get; init; }

    /// <summary>Containers to start before the first step and remove after the last (see <see cref="ScenarioService"/>).</summary>
    public IReadOnlyList<ScenarioService> Services { get; init; } = [];

    /// <summary>The commands to run, in order; the first one that fails ends the scenario.</summary>
    public required IReadOnlyList<ScenarioStep> Steps { get; init; }

    /// <summary>The scenario's folder name under tests/scenarios, which is also its test's name.</summary>
    [JsonIgnore]
    public string Name { get; init; } = "";

    /// <summary>The scenario's source folder: tests/scenarios/&lt;Name&gt;.</summary>
    [JsonIgnore]
    public string SourceFolder => Path.Combine(Repo.Scenarios, Name);

    /// <summary>
    /// Every command the scenario runs, as written in scenario.json (placeholders unexpanded):
    /// a <c>docker run</c> per service, then each step's command line.
    /// </summary>
    public IEnumerable<string> Commands =>
        Services.Select(service => $"docker run {service.Image}").Concat(Steps.Select(step => step.Run));

    /// <summary>
    /// The test's display name: the scenario's name, then every command it runs, in order, so a
    /// test list or a failure line says what the scenario does without opening its file.
    /// </summary>
    public string DisplayName => $"{Name}: {string.Join(" -> ", Commands)}";

    /// <summary>The scenarios of tests/scenarios: each subfolder that holds a scenario.json, by name.</summary>
    /// <returns>The folder names, in ordinal order (a stable test order on every machine).</returns>
    public static IReadOnlyList<string> Names() =>
        Directory.Exists(Repo.Scenarios)
            ? Directory.GetDirectories(Repo.Scenarios)
                .Where(folder => File.Exists(Path.Combine(folder, FileName)))
                .Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>Reads and checks a scenario's file.</summary>
    /// <param name="name">The scenario's folder name under tests/scenarios.</param>
    /// <returns>The scenario, with <see cref="Name"/> set.</returns>
    /// <exception cref="InvalidDataException">
    /// The file is not valid JSON, has an unknown or missing property, a step without a command,
    /// an unterminated quote, an unknown placeholder, or a service that is badly declared. The
    /// message names the file and the reason.
    /// </exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static Scenario Load(string name)
    {
        string file = Path.Combine(Repo.Scenarios, name, FileName);
        Scenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(file), JsonOptions);
        }
        catch (JsonException e)
        {
            // The serializer's message lacks the file, and for an unknown property also where it
            // is (measured: "The JSON property 'stdot' could not be mapped to any .NET member
            // contained in type 'ScenarioStep'."): both are added, the line counted from 1.
            string at = e.Path is null ? "" : $" (at {e.Path}{(e.LineNumber is { } line ? $", line {line + 1}" : "")})";
            throw new InvalidDataException($"{Repo.Relative(file)}: {e.Message}{at}", e);
        }

        if (scenario is null)
        {
            throw new InvalidDataException($"{Repo.Relative(file)}: the file holds null, not a scenario");
        }

        scenario = scenario with { Name = name };
        scenario.Validate(Repo.Relative(file));
        return scenario;
    }

    /// <summary>
    /// The display name of a scenario for test discovery, which must not throw: a scenario whose
    /// file cannot be read still becomes a test, which then fails with the reason when it runs.
    /// </summary>
    /// <param name="name">The scenario's folder name.</param>
    /// <returns><see cref="DisplayName"/>, or "&lt;name&gt;: scenario.json cannot be read".</returns>
    public static string DisplayNameOf(string name)
    {
        try
        {
            return Load(name).DisplayName;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"{name}: {FileName} cannot be read";
        }
    }

    /// <summary>
    /// Replaces the placeholders of one string from scenario.json with this machine's values.
    /// </summary>
    /// <param name="text">A command line, an expected output or a file path.</param>
    /// <param name="ports">The host port of each started service, by service name.</param>
    /// <returns>The text with every placeholder replaced.</returns>
    /// <exception cref="InvalidDataException">
    /// A placeholder is unknown, lacks its argument, or names a service that did not start
    /// (<see cref="Validate"/> rejects the first two when the file is read, so this happens only
    /// when a caller passes text that did not come from a validated scenario).
    /// </exception>
    public static string Expand(string text, IReadOnlyDictionary<string, int> ports) =>
        Placeholder().Replace(text, match =>
        {
            string kind = match.Groups[1].Value;
            string argument = match.Groups[2].Value;
            return (kind, argument.Length != 0) switch
            {
                ("rid", false) => Repo.HostRid,
                ("exe", true) => OperatingSystem.IsWindows() ? argument + ".exe" : argument,
                ("lib", true) => CrossPlatformTests.LibraryFile(Repo.HostRid, argument),
                ("port", true) when ports.TryGetValue(argument, out int port) => port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException($"unknown placeholder {match.Value}"),
            };
        });

    /// <summary>
    /// Splits a command line into the program and its arguments, without a shell: whitespace
    /// separates arguments, and double or single quotes group them (the quotes are removed).
    /// </summary>
    /// <param name="commandLine">The command line, after placeholder expansion.</param>
    /// <returns>The program, then its arguments; empty for a blank line.</returns>
    /// <exception cref="InvalidDataException">A quote is not closed.</exception>
    /// <remarks>
    /// Nothing else is special on purpose: backslashes stay as they are (Windows paths), and no
    /// variable, wildcard, pipe or redirection is interpreted, so a step means the same on every
    /// build machine. A step that needs a shell can run one (<c>sh -c '...'</c>), and is then
    /// tied to the machines that have it.
    /// </remarks>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        foreach (char c in commandLine)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                // A quote starts (or continues) a word even when it is empty: "" is an argument.
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        if (quote != '\0')
        {
            throw new InvalidDataException($"unterminated {quote} in: {commandLine}");
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>
    /// Checks what the JSON reader cannot: at least one step, a command in each, closed quotes,
    /// known placeholders with the arguments they need, and services with unique names, an
    /// image and a port.
    /// </summary>
    /// <param name="file">The scenario file, relative to the repository root, for the messages.</param>
    /// <exception cref="InvalidDataException">The first problem found, naming the file.</exception>
    private void Validate(string file)
    {
        if (string.IsNullOrWhiteSpace(Description))
        {
            throw new InvalidDataException($"{file}: \"description\" is empty: say what the scenario proves");
        }

        if (Steps.Count == 0)
        {
            throw new InvalidDataException($"{file}: \"steps\" is empty: a scenario runs at least one command");
        }

        var serviceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenarioService service in Services)
        {
            if (string.IsNullOrWhiteSpace(service.Name) || !serviceNames.Add(service.Name))
            {
                throw new InvalidDataException($"{file}: every service needs a unique \"name\" (\"{service.Name}\")");
            }

            if (string.IsNullOrWhiteSpace(service.Image))
            {
                throw new InvalidDataException($"{file}: service \"{service.Name}\" has no \"image\"");
            }

            if (service.Port is < 1 or > 65535)
            {
                throw new InvalidDataException($"{file}: service \"{service.Name}\" has port {service.Port}: expected 1-65535");
            }
        }

        for (int i = 0; i < Steps.Count; i++)
        {
            ScenarioStep step = Steps[i];
            string where = $"{file}: step {i + 1}";
            if (string.IsNullOrWhiteSpace(step.Run))
            {
                throw new InvalidDataException($"{where} has no \"run\" command");
            }

            try
            {
                if (Split(step.Run).Count == 0)
                {
                    throw new InvalidDataException("the command line is blank");
                }
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{where}: {e.Message}", e);
            }

            foreach (string text in step.Stdout.Prepend(step.Run).Concat(step.Files))
            {
                foreach (Match match in Placeholder().Matches(text))
                {
                    string kind = match.Groups[1].Value;
                    bool hasArgument = match.Groups[2].Value.Length != 0;
                    bool known = kind switch
                    {
                        "rid" => !hasArgument,
                        "exe" or "lib" => hasArgument,
                        "port" => hasArgument && serviceNames.Contains(match.Groups[2].Value),
                        _ => false,
                    };
                    if (!known)
                    {
                        throw new InvalidDataException(
                            $"{where}: unknown placeholder {match.Value} (known: {{rid}}, {{exe:name}}, {{lib:name}}, {{port:service}} of a declared service)");
                    }
                }
            }
        }
    }

    /// <summary>
    /// A placeholder: a lowercase word in braces, with an optional ":argument". Anything shaped
    /// like one is checked, so a typo such as {RID} or {libs:x} fails instead of running
    /// literally.
    /// </summary>
    /// <returns>The regex; group 1 is the kind, group 2 the argument (empty when absent).</returns>
    [GeneratedRegex(@"\{([A-Za-z]+)(?::([^{}\s]+))?\}")]
    private static partial Regex Placeholder();
}

/// <summary>One command of a scenario, and what it must produce.</summary>
internal sealed record ScenarioStep
{
    /// <summary>
    /// The command line, run in the scenario's folder without a shell (see
    /// <see cref="Scenario.Split"/>). <c>dotnet</c> gets the test environment (the isolated NuGet
    /// cache, no MSBuild node reuse); a path with a slash is a file of the scenario's folder, such
    /// as a published program (on Windows .exe, .cmd or .bat is tried when the path has no
    /// extension, as a shell would); any other name is looked up on PATH.
    /// </summary>
    public required string Run { get; init; }

    /// <summary>Why the step is there, for the test's log; optional.</summary>
    public string? Description { get; init; }

    /// <summary>The exit status the command must return; 0 when not given.</summary>
    public int ExitCode { get; init; }

    /// <summary>Texts that must each appear in the command's standard output (placeholders allowed).</summary>
    public IReadOnlyList<string> Stdout { get; init; } = [];

    /// <summary>
    /// Files or folders, relative to the scenario's folder, that must exist after the command
    /// (placeholders allowed, forward slashes on every OS).
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>
/// A container a scenario needs while its steps run, such as a server its program talks to:
/// started with <c>docker run</c> before the first step, published on a free port of
/// 127.0.0.1, and removed after the last step, whatever happened.
/// </summary>
/// <remarks>
/// A scenario with services runs only where Docker runs Linux containers of the service's
/// platform; elsewhere the test is skipped with the reason (Windows CI runners have Windows
/// containers only, macOS runners no Docker, ARM64 runners no x64 emulation).
/// </remarks>
internal sealed record ScenarioService
{
    /// <summary>The name steps use in <c>{port:name}</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The image, with its tag (pinned, like every package a scenario uses).</summary>
    public required string Image { get; init; }

    /// <summary>The image's platform: linux/amd64 unless given.</summary>
    public string Platform { get; init; } = "linux/amd64";

    /// <summary>The container port to publish; <c>{port:name}</c> is the host port it gets.</summary>
    public required int Port { get; init; }

    /// <summary>Environment variables for the container.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Text the container's log (standard output and error) shows once it serves; the steps
    /// start only after it appears. Without it the runner waits until the port accepts a
    /// connection, which Docker Desktop's port proxy may accept before the server listens.
    /// </summary>
    public string? Ready { get; init; }
}
