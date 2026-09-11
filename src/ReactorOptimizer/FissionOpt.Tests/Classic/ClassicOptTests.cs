using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>Phase 2 acceptance: the search produces self-consistent, feasible designs and is reproducible per seed.</summary>
public sealed class ClassicOptTests
{
    private static ClassicSettings Leu235(int size = 5, bool sym = true, bool activeCoolers = false)
    {
        var fuel = ClassicPresets.FindFuel("Default", "LEU-235")!;
        var s = new ClassicSettings
        {
            SizeX = size, SizeY = size, SizeZ = size,
            FuelBasePower = fuel.BasePower, FuelBaseHeat = fuel.BaseHeat,
            EnsureActiveCoolerAccessible = true, EnsureHeatNeutral = true,
            Goal = ClassicGoal.Power, SymX = sym, SymY = sym, SymZ = sym,
        };
        Array.Copy(ClassicPresets.FindCoolingRates("Default")!.Rates, s.CoolingRates, NumCoolerIds);
        Array.Fill(s.Limit, -1);
        for (int t = Active; t < Cell; ++t) s.Limit[t] = activeCoolers ? -1 : 0;
        return s;
    }

    private static ClassicOpt RunSteps(ClassicSettings s, bool useNet, int seed, int steps)
    {
        var opt = new ClassicOpt(s, useNet, seed);
        for (int i = 0; i < steps; ++i) opt.Step();
        return opt;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BestDesignIsFeasibleAndSelfConsistent(bool useNet)
    {
        var s = Leu235();
        var opt = RunSteps(s, useNet, 42, 60_000);
        var best = opt.Best;
        Assert.True(best.Value.Power > 0, "expected a non-empty design");
        Assert.True(best.Value.NetHeat <= 0.0, "heat-neutral constraint violated");

        // Re-evaluating the published state must reproduce the published metrics. Invalid tiles were
        // replaced by Air, which cannot change anything when active coolers are disabled.
        var fresh = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(best.State, fresh);
        Assert.Equal(best.Value.PowerMult, fresh.PowerMult);
        Assert.Equal(best.Value.HeatMult, fresh.HeatMult);
        Assert.Equal(best.Value.Cooling, fresh.Cooling);
        Assert.Equal(best.Value.Breed, fresh.Breed);
        Assert.Empty(fresh.InvalidTiles);
    }

    [Fact]
    public void ActiveCoolerRemovalCanOnlyImproveCooling()
    {
        // With active coolers enabled, clearing invalid tiles to Air may open new casing paths, so the
        // re-evaluated cooling may exceed (never fall below) the reported value; power is unchanged.
        var s = Leu235(activeCoolers: true);
        var opt = RunSteps(s, false, 7, 40_000);
        var fresh = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(opt.Best.State, fresh);
        Assert.Equal(opt.Best.Value.PowerMult, fresh.PowerMult);
        Assert.Equal(opt.Best.Value.HeatMult, fresh.HeatMult);
        Assert.True(fresh.Cooling >= opt.Best.Value.Cooling);
    }

    [Fact]
    public void BudgetsAreRespectedUnderSymmetry()
    {
        var s = Leu235(size: 6);
        s.Limit[Cell] = 20;
        s.Limit[Cryotheum] = 0;
        s.Limit[Redstone] = 9; // odd budget on an even-sized symmetric grid: never fully usable
        var opt = RunSteps(s, true, 3, 30_000);
        var counts = ClassicExport.CountTiles(opt.Best.State);
        Assert.True(counts[Cell] <= 20, $"cells {counts[Cell]}");
        Assert.Equal(0, counts[Cryotheum]);
        Assert.True(counts[Redstone] <= 8, $"redstone {counts[Redstone]}");
        var g = opt.Best.State;
        for (int x = 0; x < 6; ++x)
        for (int y = 0; y < 6; ++y)
        for (int z = 0; z < 6; ++z)
        {
            // Air-ed invalid tiles break perfect symmetry only where a tile was invalid, which is itself
            // symmetric, so the design stays mirror-symmetric on all three axes.
            Assert.Equal(g[x, y, z], g[5 - x, y, z]);
            Assert.Equal(g[x, y, z], g[x, 5 - y, z]);
            Assert.Equal(g[x, y, z], g[x, y, 5 - z]);
        }
    }

    [Fact]
    public void SameSeedReproducesTheRun()
    {
        var s = Leu235();
        var a = RunSteps(s, true, 123, 40_000);
        var b = RunSteps(s, true, 123, 40_000);
        Assert.Equal(a.Best.State.Data, b.Best.State.Data);
        Assert.Equal(a.Best.Value.AvgPower, b.Best.Value.AvgPower);
        Assert.Equal(a.NEpisode, b.NEpisode);
        Assert.Equal(a.NIteration, b.NIteration);
        var c = RunSteps(s, true, 124, 40_000);
        Assert.NotEqual(a.Best.State.Data, c.Best.State.Data);
    }

    [Fact]
    public void HellrageJsonRoundTripsTileNames()
    {
        var s = Leu235(size: 3);
        var g = new Grid3(3, 3, 3, Air);
        g[0, 1, 2] = Cell;
        g[1, 1, 1] = Moderator;
        g[2, 0, 0] = Water + Active;
        string json = ClassicExport.ToHellrageJson(s, g, "LEU-235");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("InteriorDimensions").GetProperty("X").GetInt32());
        var cr = root.GetProperty("CompressedReactor");
        var cell = cr.GetProperty("FuelCell")[0];
        // Planner X = internal z + 1, Y = internal x + 1, Z = internal y + 1.
        Assert.Equal(3, cell.GetProperty("X").GetInt32());
        Assert.Equal(1, cell.GetProperty("Y").GetInt32());
        Assert.Equal(2, cell.GetProperty("Z").GetInt32());
        Assert.Equal(1, cr.GetProperty("Graphite").GetArrayLength());
        Assert.Equal(1, cr.GetProperty("Active Water").GetArrayLength());
        Assert.False(cr.TryGetProperty("Air", out _));
    }
}
