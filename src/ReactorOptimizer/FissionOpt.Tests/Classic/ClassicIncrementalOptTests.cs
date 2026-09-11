using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>
/// The incremental step must be indistinguishable from the scalar step: both evaluators assemble
/// their totals from the same integer counters, so a seed must reproduce the identical run.
/// </summary>
public sealed class ClassicIncrementalOptTests
{
    private static ClassicSettings Settings(int size, bool sym, ClassicGoal goal, bool heatNeutral = true)
    {
        var fuel = ClassicPresets.FindFuel("Default", "LEU-235")!;
        var s = new ClassicSettings
        {
            SizeX = size, SizeY = size, SizeZ = size,
            FuelBasePower = fuel.BasePower, FuelBaseHeat = fuel.BaseHeat,
            EnsureActiveCoolerAccessible = true, EnsureHeatNeutral = heatNeutral,
            Goal = goal, SymX = sym, SymY = sym, SymZ = sym,
        };
        Array.Copy(ClassicPresets.FindCoolingRates("Default")!.Rates, s.CoolingRates, NumCoolerIds);
        Array.Fill(s.Limit, -1);
        for (int t = Active; t < Cell; ++t) s.Limit[t] = 0;
        return s;
    }

    [Theory]
    [InlineData(4, true, ClassicGoal.Power, true, 30_000)]
    [InlineData(6, false, ClassicGoal.Breeder, true, 15_000)]
    [InlineData(7, true, ClassicGoal.Efficiency, false, 15_000)]
    [InlineData(5, false, ClassicGoal.Power, false, 20_000)]
    public void IncrementalAndScalarRunsAgree(int size, bool sym, ClassicGoal goal, bool useNet, int steps)
    {
        var s = Settings(size, sym, goal);
        using var scalar = new ClassicOpt(s, useNet, 5, parallelChildren: false, incrementalEvaluation: false);
        using var inc = new ClassicOpt(s, useNet, 5, incrementalEvaluation: true);
        Assert.False(scalar.IncrementalEvaluation);
        Assert.True(inc.IncrementalEvaluation);
        for (int i = 1; i <= steps; ++i)
        {
            scalar.Step();
            inc.Step();
            if (i % 500 == 0 || i == steps)
            {
                Assert.True(scalar.NEpisode == inc.NEpisode && scalar.NStage == inc.NStage && scalar.NIteration == inc.NIteration,
                    $"runs diverged by step {i}: scalar ep{scalar.NEpisode}/st{scalar.NStage}/it{scalar.NIteration} vs incremental ep{inc.NEpisode}/st{inc.NStage}/it{inc.NIteration}");
                Assert.Equal(scalar.Best.State.Data, inc.Best.State.Data);
                Assert.Equal(scalar.Best.Value.Breed, inc.Best.Value.Breed);
                Assert.Equal(scalar.Best.Value.PowerMult, inc.Best.Value.PowerMult);
                Assert.Equal(scalar.Best.Value.HeatMult, inc.Best.Value.HeatMult);
                Assert.Equal(scalar.Best.Value.Cooling, inc.Best.Value.Cooling);
                Assert.Equal(scalar.LossHistory.ToArray(), inc.LossHistory.ToArray());
            }
        }
    }

    [Fact]
    public void BestDesignIsSelfConsistent()
    {
        var s = Settings(6, true, ClassicGoal.Power);
        using var opt = new ClassicOpt(s, true, 11, incrementalEvaluation: true);
        for (int i = 0; i < 40_000; ++i) opt.Step();
        var fresh = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(opt.Best.State, fresh);
        Assert.True(opt.Best.Value.Power > 0);
        Assert.True(fresh.NetHeat <= 0);
        Assert.Equal(opt.Best.Value.Breed, fresh.Breed);
        Assert.Equal(opt.Best.Value.PowerMult, fresh.PowerMult, 1e-9);
        Assert.Equal(opt.Best.Value.Cooling, fresh.Cooling, 1e-9);
        Assert.Empty(fresh.InvalidTiles);
    }

    [Fact]
    public void AutoModeFollowsSupports()
    {
        var s = Settings(5, true, ClassicGoal.Power);
        using var a = new ClassicOpt(s, false, 1);
        Assert.True(a.IncrementalEvaluation);
        Assert.False(a.ParallelChildren);
        s.Limit[Water + Active] = -1; // active coolers + accessibility: unsupported, falls back
        using var b = new ClassicOpt(s, false, 1);
        Assert.False(b.IncrementalEvaluation);
        Assert.Throws<ArgumentException>(() => new ClassicOpt(s, false, 1, incrementalEvaluation: true));
    }
}
