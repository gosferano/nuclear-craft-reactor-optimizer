using System.Globalization;
using System.Text;
using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Tests.Oracle;
using Xunit.Abstractions;

namespace FissionOpt.Tests.Classic;

/// <summary>
/// Differential fuzz test: the C# classic evaluator must match the C++ oracle on every output
/// field over many random (settings, state) pairs: integers and the invalid-tile list exactly, the
/// double totals to 1e-9 relative (the port assembles them from integer counters rather than in
/// scan order, so the last bit can differ from the C++).
///
/// Case count and seed are overridable via FISSIONOPT_FUZZ_CASES / FISSIONOPT_FUZZ_SEED.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class ClassicDifferentialTests
{
    private readonly OracleProcess _oracle;
    private readonly ITestOutputHelper _output;

    public ClassicDifferentialTests(OracleProcess oracle, ITestOutputHelper output)
    {
        _oracle = oracle;
        _output = output;
    }

    public static int CaseCount => EnvInt("FISSIONOPT_FUZZ_CASES", 100_000);
    public static int Seed => EnvInt("FISSIONOPT_FUZZ_SEED", 20200815);

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [Fact]
    public void RandomStatesMatchOracle()
    {
        int cases = CaseCount;
        var rng = new Random(Seed);
        var evaluation = new ClassicEvaluation();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < cases; ++i)
        {
            var (settings, state) = ClassicFuzz.Next(rng);
            var evaluator = new ClassicEvaluator(settings);
            evaluator.Run(state, evaluation);
            var expected = ClassicOracle.Evaluate(_oracle, settings, state);
            AssertMatches(expected, evaluation, settings, state, i);
        }
        _output.WriteLine($"{cases} classic cases matched the oracle in {sw.Elapsed.TotalSeconds:F1}s (seed {Seed})");
    }

    /// <summary>The same evaluator instance must give identical answers when reused (scratch buffers reset correctly).</summary>
    [Fact]
    public void ReusedEvaluatorMatchesOracle()
    {
        var rng = new Random(Seed + 1);
        var evaluation = new ClassicEvaluation();
        int cases = Math.Min(CaseCount, 5_000);
        for (int i = 0; i < cases; ++i)
        {
            var (settings, state) = ClassicFuzz.Next(rng);
            var evaluator = new ClassicEvaluator(settings);
            for (int k = 0; k < 4; ++k)
            {
                // Perturb a few tiles between runs so stale scratch state would show up.
                for (int m = 0; m < 3; ++m)
                    state.Data[rng.Next(state.Length)] = rng.Next(ClassicTiles.Air + 1);
                evaluator.Run(state, evaluation);
                var expected = ClassicOracle.Evaluate(_oracle, settings, state);
                AssertMatches(expected, evaluation, settings, state, i);
            }
        }
    }

    private static void AssertMatches(ClassicOracleResult e, ClassicEvaluation a, ClassicSettings settings, Grid3 state, int caseIndex)
    {
        var diffs = new StringBuilder();
        Check(diffs, "powerMult", e.PowerMult, a.PowerMult);
        Check(diffs, "heatMult", e.HeatMult, a.HeatMult);
        Check(diffs, "cooling", e.Cooling, a.Cooling);
        if (e.Breed != a.Breed) diffs.Append($"breed: oracle {e.Breed} vs ours {a.Breed}\n");
        Check(diffs, "heat", e.Heat, a.Heat);
        Check(diffs, "netHeat", e.NetHeat, a.NetHeat);
        Check(diffs, "dutyCycle", e.DutyCycle, a.DutyCycle);
        Check(diffs, "avgMult", e.AvgMult, a.AvgMult);
        Check(diffs, "power", e.Power, a.Power);
        Check(diffs, "avgPower", e.AvgPower, a.AvgPower);
        Check(diffs, "avgBreed", e.AvgBreed, a.AvgBreed);
        Check(diffs, "efficiency", e.Efficiency, a.Efficiency);
        if (!e.InvalidTiles.SequenceEqual(a.InvalidTiles))
        {
            diffs.Append($"invalidTiles: oracle [{string.Join(", ", e.InvalidTiles)}]\n");
            diffs.Append($"              ours   [{string.Join(", ", a.InvalidTiles)}]\n");
            var onlyOracle = e.InvalidTiles.Except(a.InvalidTiles).ToList();
            var onlyOurs = a.InvalidTiles.Except(e.InvalidTiles).ToList();
            if (onlyOracle.Count > 0) diffs.Append($"  only oracle: {string.Join(", ", onlyOracle.Select(c => $"{c}={TileName(state[c])}"))}\n");
            if (onlyOurs.Count > 0) diffs.Append($"  only ours:   {string.Join(", ", onlyOurs.Select(c => $"{c}={TileName(state[c])}"))}\n");
        }
        if (diffs.Length == 0) return;

        string request = ClassicOracle.FormatRequest(settings, state);
        string dump = Path.Combine(AppContext.BaseDirectory, $"classic-mismatch-{caseIndex}.txt");
        File.WriteAllText(dump, request);
        Assert.Fail($"Classic evaluator mismatch on fuzz case {caseIndex} ({settings.SizeX}x{settings.SizeY}x{settings.SizeZ}, accessible={settings.EnsureActiveCoolerAccessible}):\n{diffs}Oracle request written to {dump}\n{request}");
    }

    private static void Check(StringBuilder diffs, string name, double expected, double actual)
    {
        if (BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual)) return; // also covers NaN/inf
        if (double.IsFinite(expected) && double.IsFinite(actual) && Math.Abs(expected - actual) <= 1e-9 * Math.Max(1.0, Math.Abs(expected))) return;
        diffs.Append($"{name}: oracle {expected:R} vs ours {actual:R}\n");
    }

    private static string TileName(int t) => t switch
    {
        ClassicTiles.Cell => "Cell",
        ClassicTiles.Moderator => "Moderator",
        ClassicTiles.Air => "Air",
        >= ClassicTiles.Active => $"Active{t - ClassicTiles.Active}",
        _ => $"Cooler{t}",
    };
}
