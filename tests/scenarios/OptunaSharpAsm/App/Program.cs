// An OptunaSharp study (TPE sampler, a fixed seed) minimizes objective_eval, computed by the asm
// library, over x and y in [-10, 10] in 100 trials. Every evaluation is also computed in C# and
// must agree; the best value must come within 1.0 of the minimum (0 at x = 2, y = -1), which
// TPE reaches with a wide margin. Exit status 0 when both hold.
using System.Globalization;
using System.Runtime.InteropServices;
using OptunaSharp;
using OptunaSharp.Logging;
using OptunaSharp.Samplers;

// One line per trial at the default level: the scenario's output stays readable without them.
OptunaLogging.SetVerbosity((int)LogLevel.Warning);

int mismatches = 0;
Study study = Optuna.CreateStudy(sampler: new TPESampler(seed: 20261121), direction: StudyDirection.Minimize);
study.Optimize(trial =>
{
    double x = trial.SuggestFloat("x", -10, 10);
    double y = trial.SuggestFloat("y", -10, 10);
    double value = Objective.Eval(x, y);
    // C#'s own evaluation, operation for operation: no FMA on either side, so the bits match.
    double expected = (x - 2) * (x - 2) + (y + 1) * (y + 1);
    mismatches += BitConverter.DoubleToInt64Bits(value) == BitConverter.DoubleToInt64Bits(expected) ? 0 : 1;
    return value;
}, nTrials: 100);

double bestX = Convert.ToDouble(study.BestParams["x"], CultureInfo.InvariantCulture);
double bestY = Convert.ToDouble(study.BestParams["y"], CultureInfo.InvariantCulture);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"best value {study.BestValue:F4} at x = {bestX:F3}, y = {bestY:F3} after {study.Trials.Count} trials"));
Console.WriteLine(mismatches == 0 ? "asm and C# agree on every evaluation" : $"asm and C# DISAGREE on {mismatches} evaluations");
bool found = study.BestValue < 1.0;
Console.WriteLine(found ? "OptunaSharp minimized the asm objective" : "OptunaSharp did NOT get near the minimum");
return mismatches == 0 && found ? 0 : 1;

/// <summary>The export of the asm library objective.</summary>
internal static partial class Objective
{
    /// <summary>(x - 2)^2 + (y + 1)^2, computed in asm.</summary>
    /// <param name="x">The first parameter.</param>
    /// <param name="y">The second parameter.</param>
    /// <returns>The value: 0 at (2, -1), positive elsewhere.</returns>
    [LibraryImport("objective", EntryPoint = "objective_eval")]
    internal static partial double Eval(double x, double y);
}
