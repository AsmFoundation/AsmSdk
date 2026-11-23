using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The scenario format's own rules (tests/scenarios/scenario.schema.json,
/// docs/ecosystem/scenarios.md), on texts written here: the "stderr" and "absent" step fields,
/// the {version} placeholder, and the checks that turn a typo into an error rather than a step
/// that checks less. The scenarios themselves (ScenarioTests) only show that valid files run.
/// </summary>
public sealed class ScenarioFormatTests
{
    /// <summary>A scenario with one step, as text: the step's JSON object goes in as given.</summary>
    /// <param name="step">The step's JSON object.</param>
    /// <returns>The scenario file's text.</returns>
    private static string OneStep(string step) =>
        $$"""{ "description": "format test", "steps": [ {{step}} ] }""";

    /// <summary>Parses a scenario text under a made-up file name.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The scenario.</returns>
    private static Scenario Parse(string json) => Scenario.Parse(json, "FormatTest", "tests/scenarios/FormatTest/scenario.json");

    /// <summary>"stderr" and "absent" are read into the step, with their placeholders kept for the run.</summary>
    [Fact]
    public void Stderr_and_absent_are_step_fields()
    {
        Scenario scenario = Parse(OneStep("""{ "run": "dotnet add reference x", "exitCode": 1, "stderr": ["incompatible"], "absent": ["ASM0013", "{rid}"] }"""));
        ScenarioStep step = Assert.Single(scenario.Steps);
        Assert.Equal(1, step.ExitCode);
        Assert.Equal(["incompatible"], step.Stderr);
        Assert.Equal(["ASM0013", "{rid}"], step.Absent);
    }

    /// <summary>{version} expands to the AsmFoundation.Sdk version under test, the version the templates package has too.</summary>
    [Fact]
    public void The_version_placeholder_is_the_sdk_version_under_test()
    {
        Parse(OneStep("""{ "run": "dotnet new install AsmFoundation.Templates@{version}" }"""));
        Assert.Equal($"AsmFoundation.Templates@{Repo.SdkVersion()}",
            Scenario.Expand("AsmFoundation.Templates@{version}", new Dictionary<string, int>()));
    }

    /// <summary>
    /// A misspelt or misused placeholder is an error that lists the known ones, {version} among
    /// them: {version} takes no argument, and {versions} is no placeholder.
    /// </summary>
    /// <param name="text">The command line with the bad placeholder.</param>
    [Theory]
    [InlineData("dotnet new install AsmFoundation.Templates@{versions}")]
    [InlineData("dotnet new install AsmFoundation.Templates@{version:x}")]
    public void A_bad_placeholder_is_an_error_naming_the_known_ones(string text)
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(OneStep($$"""{ "run": "{{text}}" }""")));
        Assert.Contains("unknown placeholder", error.Message);
        Assert.Contains("{version}", error.Message);
        Assert.StartsWith("tests/scenarios/FormatTest/scenario.json: step 1", error.Message);
    }

    /// <summary>
    /// An empty text in "stdout", "stderr" or "absent" is an error: as an expectation it would
    /// match any output, and as an absent text it would fail every step.
    /// </summary>
    /// <param name="field">The field holding the empty text.</param>
    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("absent")]
    public void An_empty_text_is_an_error(string field)
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(OneStep($$"""{ "run": "dotnet build", "{{field}}": [""] }""")));
        Assert.Contains("has an empty text", error.Message);
    }

    /// <summary>An unknown step field (a misspelt "stderr") is an error that says where it is, never silently skipped.</summary>
    [Fact]
    public void A_misspelt_field_is_an_error()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(OneStep("""{ "run": "dotnet build", "stdrr": ["x"] }""")));
        Assert.Contains("stdrr", error.Message);
        Assert.Contains("at $.steps[0].stdrr", error.Message);
    }
}
