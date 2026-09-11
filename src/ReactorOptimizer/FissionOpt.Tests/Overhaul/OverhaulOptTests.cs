using FissionOpt.Core;
using FissionOpt.Core.Overhaul;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Tests.Overhaul;

/// <summary>Phase 5 acceptance for the overhaul search: feasible, self-consistent, budget-respecting, reproducible.</summary>
public sealed class OverhaulOptTests
{
    private static OverhaulSettings Leu235(int size = 5, bool controllable = false)
    {
        var p = OverhaulPresets.Find("OX", "LEU-235")!;
        var s = new OverhaulSettings
        {
            SizeX = size, SizeY = size, SizeZ = size,
            Goal = OverhaulGoal.Output, Controllable = controllable, SymX = true, SymY = true, SymZ = true,
        };
        s.Fuels.Add(new OverhaulFuel(p.EfficiencyPercent / 100, -1, p.Criticality, p.Heat, p.SelfPriming, p.Name));
        Array.Fill(s.Limits, -1);
        s.SourceLimits[0] = -1; s.SourceLimits[1] = 0; s.SourceLimits[2] = 0;
        return s;
    }

    private static OverhaulOpt RunSteps(OverhaulSettings s, int seed, int steps)
    {
        var opt = new OverhaulOpt(s, seed);
        for (int i = 0; i < steps; ++i) opt.Step();
        return opt;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BestDesignIsFeasibleAndSelfConsistent(bool controllable)
    {
        var s = Leu235(controllable: controllable);
        var opt = RunSteps(s, 42, 150_000);
        var best = opt.Best;
        Assert.True(best.Value.Output > 0, "expected a non-empty design");
        Assert.Equal(0, best.Value.TotalPositiveNetHeat);

        // best.State was canonicalized (non-functional tiles stripped); re-evaluating it must give the
        // same functional result: same active cells, no positive net heat, same output.
        var fresh = new OverhaulEvaluation();
        fresh.Initialize(s, false);
        fresh.Run(best.State);
        Assert.Equal(best.Value.NActiveCells, fresh.NActiveCells);
        Assert.Equal(0, fresh.TotalPositiveNetHeat);
        Assert.Equal(best.Value.Output, fresh.Output, 9);
        Assert.Equal(best.Value.Efficiency, fresh.Efficiency, 9);
        if (controllable)
        {
            var shielded = new OverhaulEvaluation();
            shielded.Initialize(s, true);
            shielded.Run(best.State);
            Assert.Equal(0, shielded.NActiveCells);
        }
        // Nothing non-functional survives canonicalization.
        var canon = best.State.Clone();
        fresh.Canonicalize(canon);
        Assert.Equal(best.State.Data, canon.Data);
    }

    [Fact]
    public void BudgetsAreRespected()
    {
        var s = Leu235(size: 6);
        s.Fuels[0].Limit = 12;
        s.Limits[Wt] = 0;
        s.Limits[M0] = 9; // odd budget on an even symmetric grid: never fully usable
        s.SourceLimits[0] = 4;
        var opt = RunSteps(s, 3, 80_000);
        var counts = OverhaulExport.BlockCounts(s, opt.Best.State);
        int Count(string name) => counts.FirstOrDefault(c => c.name == name).count;
        Assert.True(Count("Cell") <= 12, $"cells {Count("Cell")}");
        Assert.True(Count("Cf-252") <= 4, $"sources {Count("Cf-252")}");
        Assert.Equal(0, Count("Water"));
        Assert.True(Count("Graphite") <= 8, $"graphite {Count("Graphite")}");
    }

    [Fact]
    public void SameSeedReproducesTheRun()
    {
        var s = Leu235();
        var a = RunSteps(s, 123, 60_000);
        var b = RunSteps(s, 123, 60_000);
        Assert.Equal(a.Best.State.Data, b.Best.State.Data);
        Assert.Equal(a.Best.Value.Output, b.Best.Value.Output);
        Assert.Equal(a.NIteration, b.NIteration);
        var c = RunSteps(s, 124, 60_000);
        Assert.NotEqual(a.Best.State.Data, c.Best.State.Data);
    }

    [Fact]
    public void PlannerJsonHasTheExpectedShape()
    {
        var s = Leu235(size: 3);
        s.Compute();
        var g = new Grid3(3, 3, 3, Air);
        g[0, 1, 2] = C0 + 1; // LEU-235 primed by Cf-252
        g[1, 1, 1] = M0;
        g[2, 0, 0] = Wt;
        g[2, 2, 2] = Conductor;
        g[0, 0, 0] = Irradiator;
        string json = OverhaulExport.ToPlannerJson(s, g);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("Data");
        Assert.Equal(2, doc.RootElement.GetProperty("SaveVersion").GetProperty("Major").GetInt32());
        var cell = data.GetProperty("FuelCells").GetProperty("[OX]LEU-235;True;Cf-252")[0];
        Assert.Equal(3, cell.GetProperty("X").GetInt32());
        Assert.Equal(1, cell.GetProperty("Y").GetInt32());
        Assert.Equal(2, cell.GetProperty("Z").GetInt32());
        Assert.Equal(1, data.GetProperty("Moderators").GetProperty("Graphite").GetArrayLength());
        Assert.Equal(1, data.GetProperty("HeatSinks").GetProperty("Water").GetArrayLength());
        Assert.Equal(1, data.GetProperty("Conductors").GetArrayLength());
        Assert.Equal(1, data.GetProperty("Irradiators").GetProperty("{\"HeatPerFlux\":0,\"EfficiencyMultiplier\":0.0}").GetArrayLength());
        Assert.Equal("1A", OverhaulExport.Label(s, C0 + 1));
        Assert.Equal("1.", OverhaulExport.Label(s, C0));
        Assert.Equal("B.", OverhaulExport.Label(s, B));
    }
}
