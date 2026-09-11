using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>Parallel child evaluation must be an invisible implementation detail: same seed, same run, step for step.</summary>
public sealed class ClassicParallelTests
{
    private static ClassicSettings Settings(int size, bool sym, ClassicGoal goal)
    {
        var fuel = ClassicPresets.FindFuel("Default", "LEU-235")!;
        var s = new ClassicSettings
        {
            SizeX = size, SizeY = size, SizeZ = size,
            FuelBasePower = fuel.BasePower, FuelBaseHeat = fuel.BaseHeat,
            EnsureActiveCoolerAccessible = true, EnsureHeatNeutral = true,
            Goal = goal, SymX = sym, SymY = sym, SymZ = sym,
        };
        Array.Copy(ClassicPresets.FindCoolingRates("Default")!.Rates, s.CoolingRates, NumCoolerIds);
        Array.Fill(s.Limit, -1);
        for (int t = Active; t < Cell; ++t) s.Limit[t] = t % 2 == 0 ? 0 : -1; // a few active coolers so accessibility runs too
        return s;
    }

    [Theory]
    [InlineData(4, true, ClassicGoal.Power, true, 40_000)]
    [InlineData(6, false, ClassicGoal.Breeder, true, 20_000)]
    [InlineData(9, true, ClassicGoal.Efficiency, false, 6_000)]
    public void ParallelAndSequentialRunsAreIdentical(int size, bool sym, ClassicGoal goal, bool useNet, int steps)
    {
        var s = Settings(size, sym, goal);
        using var seq = new ClassicOpt(s, useNet, 99, parallelChildren: false);
        using var par = new ClassicOpt(s, useNet, 99, parallelChildren: true);
        Assert.False(seq.ParallelChildren);
        Assert.True(par.ParallelChildren);
        for (int i = 1; i <= steps; ++i)
        {
            seq.Step();
            par.Step();
            if (i % 1000 == 0 || i == steps)
            {
                Assert.Equal(seq.NEpisode, par.NEpisode);
                Assert.Equal(seq.NStage, par.NStage);
                Assert.Equal(seq.NIteration, par.NIteration);
                Assert.Equal(seq.Best.State.Data, par.Best.State.Data);
                Assert.Equal(seq.Best.Value.AvgPower, par.Best.Value.AvgPower);
                Assert.Equal(seq.Best.Value.InvalidTiles, par.Best.Value.InvalidTiles);
                Assert.Equal(seq.LossHistory.ToArray(), par.LossHistory.ToArray());
            }
        }
    }

    [Fact]
    public void AutoModeFollowsTheVolumeThreshold()
    {
        using var small = new ClassicOpt(Settings(5, true, ClassicGoal.Power), false, 1);
        using var large = new ClassicOpt(Settings(13, true, ClassicGoal.Power), false, 1);
        Assert.False(small.ParallelChildren);
        Assert.True(large.ParallelChildren);
    }
}
