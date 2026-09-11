using System.Globalization;
using System.Text;
using FissionOpt.Core;
using FissionOpt.Core.Overhaul;
using FissionOpt.Tests.Oracle;
using Xunit.Abstractions;

namespace FissionOpt.Tests.Overhaul;

/// <summary>
/// Differential fuzz test for the overhaul evaluator: every scalar, every cluster stat, every
/// per-tile field, every flux edge and the canonicalized state must match the C++ oracle.
/// Cluster tile lists are compared in order: the port reproduces the C++ DFS order exactly.
/// </summary>
[Collection(OracleCollection.Name)]
public sealed class OverhaulDifferentialTests
{
    private readonly OracleProcess _oracle;
    private readonly ITestOutputHelper _output;

    public OverhaulDifferentialTests(OracleProcess oracle, ITestOutputHelper output)
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
        var rng = new Random(Seed + 7);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eval = new OverhaulEvaluation();
        var canon = new Grid3(1, 1, 1);
        for (int i = 0; i < cases; ++i)
        {
            var (settings, shieldOn, state) = OverhaulFuzz.Next(rng);
            eval.Initialize(settings, shieldOn);
            eval.Run(state);
            if (!canon.SameShape(state)) canon = new Grid3(state.SizeX, state.SizeY, state.SizeZ);
            canon.CopyFrom(state);
            eval.Canonicalize(canon);
            var expected = OverhaulOracle.Evaluate(_oracle, settings, shieldOn, state);
            AssertMatches(expected, eval, canon, settings, shieldOn, state, i);
        }
        _output.WriteLine($"{cases} overhaul cases matched the oracle in {sw.Elapsed.TotalSeconds:F1}s (seed {Seed + 7})");
    }

    /// <summary>Re-running one initialized evaluation on perturbed states must reset all per-tile scratch state.</summary>
    [Fact]
    public void ReusedEvaluationMatchesOracle()
    {
        var rng = new Random(Seed + 8);
        int cases = Math.Min(CaseCount, 3_000);
        var eval = new OverhaulEvaluation();
        for (int i = 0; i < cases; ++i)
        {
            var (settings, shieldOn, state) = OverhaulFuzz.Next(rng);
            eval.Initialize(settings, shieldOn);
            var canon = new Grid3(state.SizeX, state.SizeY, state.SizeZ);
            for (int k = 0; k < 4; ++k)
            {
                for (int m = 0; m < 3; ++m)
                    state.Data[rng.Next(state.Length)] = rng.Next(OverhaulTiles.C0 + settings.CellTypes.Count);
                eval.Run(state);
                canon.CopyFrom(state);
                eval.Canonicalize(canon);
                var expected = OverhaulOracle.Evaluate(_oracle, settings, shieldOn, state);
                AssertMatches(expected, eval, canon, settings, shieldOn, state, i);
            }
        }
    }

    private static void AssertMatches(OverhaulOracleResult e, OverhaulEvaluation a, Grid3 canon, OverhaulSettings settings, bool shieldOn, Grid3 state, int caseIndex)
    {
        var d = new StringBuilder();
        // Settings.compute
        if (!e.CellTypes.SequenceEqual(settings.CellTypes)) d.Append("cellTypes differ\n");
        CheckD(d, "maxOutput", e.MaxOutput, settings.MaxOutput);
        CheckI(d, "minCriticality", e.MinCriticality, settings.MinCriticality);
        CheckI(d, "minHeat", e.MinHeat, settings.MinHeat);
        // Scalars
        CheckD(d, "rawEfficiency", e.RawEfficiency, a.RawEfficiency);
        CheckD(d, "efficiency", e.Efficiency, a.Efficiency);
        CheckD(d, "rawOutput", e.RawOutput, a.RawOutput);
        CheckD(d, "output", e.Output, a.Output);
        CheckD(d, "density", e.Density, a.Density);
        CheckD(d, "sparsityPenalty", e.SparsityPenalty, a.SparsityPenalty);
        CheckI(d, "nFunctionalBlocks", e.NFunctionalBlocks, a.NFunctionalBlocks);
        CheckI(d, "totalPositiveNetHeat", e.TotalPositiveNetHeat, a.TotalPositiveNetHeat);
        CheckI(d, "irradiatorFlux", e.IrradiatorFlux, a.IrradiatorFlux);
        CheckI(d, "nActiveCells", e.NActiveCells, a.NActiveCells);
        CheckI(d, "totalRawFlux", e.TotalRawFlux, a.TotalRawFlux);
        CheckI(d, "maxCellFlux", e.MaxCellFlux, a.MaxCellFlux);
        // Flux roots (same scan order on both sides)
        var roots = a.FluxRoots.Select(a.CoordOf).ToList();
        if (!e.FluxRoots.SequenceEqual(roots)) d.Append($"fluxRoots: oracle [{string.Join(", ", e.FluxRoots)}] vs ours [{string.Join(", ", roots)}]\n");
        // Clusters
        CheckI(d, "nClusters", e.Clusters.Count, a.ClusterCount);
        for (int k = 0; k < Math.Min(e.Clusters.Count, a.ClusterCount); ++k)
        {
            var ec = e.Clusters[k];
            var ac = a.GetCluster(k);
            string p = $"cluster[{k}].";
            CheckD(d, p + "rawOutput", ec.RawOutput, ac.RawOutput);
            CheckD(d, p + "coolingPenaltyMult", ec.CoolingPenaltyMult, ac.CoolingPenaltyMult);
            CheckD(d, p + "output", ec.Output, ac.Output);
            CheckD(d, p + "rawEfficiency", ec.RawEfficiency, ac.RawEfficiency);
            CheckD(d, p + "efficiency", ec.Efficiency, ac.Efficiency);
            CheckI(d, p + "heat", ec.Heat, ac.Heat);
            CheckI(d, p + "cooling", ec.Cooling, ac.Cooling);
            CheckI(d, p + "netHeat", ec.NetHeat, ac.NetHeat);
            if (ec.HasCasingConnection != ac.HasCasingConnection) d.Append($"{p}hasCasingConnection: oracle {ec.HasCasingConnection} vs ours {ac.HasCasingConnection}\n");
            var et = ec.Tiles;
            var at = ac.Tiles.Select(a.CoordOf).ToList();
            if (!et.SequenceEqual(at)) d.Append($"{p}tiles: oracle [{string.Join(", ", et)}] vs ours [{string.Join(", ", at)}]\n");
        }
        // Tiles
        for (int i = 0; i < state.Length && d.Length < 4000; ++i)
        {
            var t = e.Tiles[i];
            string p = $"tile{a.CoordOf(i)}({t.Kind}).";
            char kind = a.KindAt(i) switch
            {
                TileKind.Air => 'A', TileKind.Cell => 'C', TileKind.Moderator => 'M', TileKind.Reflector => 'R',
                TileKind.Shield => 'S', TileKind.Irradiator => 'I', TileKind.Conductor => 'K', _ => 'H',
            };
            if (kind != t.Kind) { d.Append($"{p}kind: ours {kind}\n"); continue; }
            switch (t.Kind)
            {
                case 'C':
                    CheckI(d, p + "neutronSource", t.NeutronSource, a.NeutronSourceAt(i));
                    CheckB(d, p + "blocked", t.Blocked, a.IsNeutronSourceBlockedAt(i));
                    CheckB(d, p + "excluded", t.Excluded, a.IsExcludedFromFluxRootsAt(i));
                    CheckB(d, p + "active", t.Active, a.IsActiveAt(i));
                    CheckI(d, p + "flux", t.Flux, a.FluxAt(i));
                    CheckI(d, p + "heatMult", t.HeatMult, a.HeatMultAt(i));
                    CheckI(d, p + "cluster", t.Cluster, a.ClusterAt(i));
                    CheckD(d, p + "positionalEfficiency", t.PositionalEfficiency, a.PositionalEfficiencyAt(i));
                    if (t.Cluster >= 0)
                    {
                        CheckD(d, p + "fluxEfficiency", t.FluxEfficiency, a.FluxEfficiencyAt(i));
                        CheckD(d, p + "efficiency", t.Efficiency, a.EfficiencyAt(i));
                    }
                    for (int dir = 0; dir < 6; ++dir)
                    {
                        var ee = t.Edges[dir];
                        string q = $"{p}edge[{dir}].";
                        CheckB(d, q + "has", ee.Has, a.EdgeHas(i, dir));
                        if (!ee.Has || !a.EdgeHas(i, dir)) continue;
                        var (eff, flux, nMod, refl) = a.Edge(i, dir);
                        CheckD(d, q + "efficiency", ee.Efficiency, eff);
                        CheckI(d, q + "flux", ee.Flux, flux);
                        CheckI(d, q + "nModerators", ee.NModerators, nMod);
                        CheckB(d, q + "isReflected", ee.IsReflected, refl);
                    }
                    break;
                case 'M':
                    CheckB(d, p + "active", t.Active, a.IsActiveAt(i));
                    CheckB(d, p + "functional", t.Functional, a.IsFunctionalAt(i));
                    break;
                case 'R':
                    CheckB(d, p + "active", t.Active, a.IsActiveAt(i));
                    break;
                case 'S': case 'I':
                    CheckI(d, p + "flux", t.Flux, a.FluxAt(i));
                    CheckI(d, p + "cluster", t.Cluster, a.ClusterAt(i));
                    break;
                case 'K':
                    CheckI(d, p + "cluster", t.Cluster, a.ClusterAt(i));
                    break;
                case 'H':
                    CheckB(d, p + "active", t.Active, a.IsActiveAt(i));
                    CheckI(d, p + "cluster", t.Cluster, a.ClusterAt(i));
                    break;
            }
        }
        if (!e.Canonical.SequenceEqual(canon.Data)) d.Append("canonicalized state differs\n");
        if (d.Length == 0) return;

        string request = OverhaulOracle.FormatRequest(settings, shieldOn, state);
        string dump = Path.Combine(AppContext.BaseDirectory, $"overhaul-mismatch-{caseIndex}.txt");
        File.WriteAllText(dump, request);
        Assert.Fail($"Overhaul evaluator mismatch on fuzz case {caseIndex} ({settings.SizeX}x{settings.SizeY}x{settings.SizeZ}, shieldOn={shieldOn}):\n{d}Oracle request written to {dump}");
    }

    private static void CheckD(StringBuilder d, string name, double expected, double actual)
    {
        if (BitConverter.DoubleToInt64Bits(expected) != BitConverter.DoubleToInt64Bits(actual))
            d.Append($"{name}: oracle {expected:R} vs ours {actual:R}\n");
    }

    private static void CheckI(StringBuilder d, string name, int expected, int actual)
    {
        if (expected != actual) d.Append($"{name}: oracle {expected} vs ours {actual}\n");
    }

    private static void CheckB(StringBuilder d, string name, bool expected, bool actual)
    {
        if (expected != actual) d.Append($"{name}: oracle {expected} vs ours {actual}\n");
    }
}
