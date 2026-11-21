using AsmSdk.Tests.Infrastructure;
using Xunit;

namespace AsmSdk.Tests;

/// <summary>
/// The scenarios of tests/scenarios, one test each: a whole small repository (a .slnx with
/// .csproj and .asmproj projects, sometimes packages of our other projects from nuget.org) is
/// copied to artifacts/scenarios and taken through the commands its scenario.json lists
/// (dotnet build, run, test, publish, pack, tool install, the programs built), each checked for
/// its exit status, its output and the files it must leave. The test's name lists every
/// command, and its log shows each with its output.
/// </summary>
/// <remarks>
/// <para>
/// Where the other tests check one SDK rule each, a scenario checks that the rules add up to
/// what a user does: build an app with an asm library and run it, unit-test asm code, publish,
/// pack a tool and install it, beside real packages (NumSharp, KvAsm.Client, OptunaSharp) that
/// bring native assets and dependencies of their own.
/// </para>
/// <para>
/// Adding a scenario takes no C#: a folder with its projects and a scenario.json
/// (docs/ecosystem/scenarios.md; tests/scenarios/scenario.schema.json for editors). One
/// scenario runs alone with <c>--filter-trait "Scenario=&lt;Name&gt;"</c>.
/// </para>
/// </remarks>
/// <param name="sdk">The SDK under test (assembly fixture): the local feed the scenarios restore from.</param>
/// <param name="log">The test's log: every command, its exit status, duration and output.</param>
public sealed class ScenarioTests(SdkUnderTest sdk, ITestOutputHelper log)
{
    /// <summary>
    /// One row per scenario folder, named by its commands, with the trait Scenario=&lt;Name&gt;
    /// for filtering. A scenario whose file cannot be read still gets a row: its test fails with
    /// the reason, rather than disappearing from the run.
    /// </summary>
    /// <remarks>
    /// Each row is a test of its own at discovery (one per scenario in an IDE's test list, and
    /// what the trait filter selects) because xunit.runner.json turns preEnumerateTheories on;
    /// xunit v3's default would show one test until it runs. xunit appends the argument to the
    /// display name ("(scenario: \"Name\")"), so the name ends with a space before it.
    /// </remarks>
    public static TheoryData<string> All =>
        new(Scenario.Names().Select(name =>
            new TheoryDataRow<string>(name)
                .WithTestDisplayName(Scenario.DisplayNameOf(name) + " ")
                .WithTrait("Scenario", name)));

    /// <summary>
    /// Runs one scenario's commands in order on a fresh copy of its folder: each must exit as
    /// expected, print what it must, and leave the files it must. Skipped with the reason where
    /// the scenario cannot run (its rids, or Docker for its services).
    /// </summary>
    /// <param name="scenario">The scenario's folder name under tests/scenarios.</param>
    [Theory]
    [MemberData(nameof(All))]
    public void Scenario_runs_as_written(string scenario)
    {
        Scenario loaded = Scenario.Load(scenario);
        if (ScenarioRunner.SkipReason(loaded) is { } reason)
        {
            Assert.Skip(reason);
        }

        ScenarioRunner.Run(loaded, sdk, log);
    }
}
