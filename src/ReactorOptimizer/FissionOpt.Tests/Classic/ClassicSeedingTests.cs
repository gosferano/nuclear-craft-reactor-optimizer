using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

public sealed class ClassicSeedingTests
{
    private static ClassicSettings Settings(int x, int y, int z, bool sym)
    {
        var fuel = ClassicPresets.FindFuel("Default", "LEU-235")!;
        var s = new ClassicSettings
        {
            SizeX = x, SizeY = y, SizeZ = z, FuelBasePower = fuel.BasePower, FuelBaseHeat = fuel.BaseHeat,
            EnsureActiveCoolerAccessible = true, EnsureHeatNeutral = true, Goal = ClassicGoal.Breeder, SymX = sym, SymY = sym, SymZ = sym,
        };
        Array.Copy(ClassicPresets.FindCoolingRates("Default")!.Rates, s.CoolingRates, NumCoolerIds);
        Array.Fill(s.Limit, -1);
        for (int t = Active; t < Cell; ++t) s.Limit[t] = 0;
        return s;
    }

    [Theory]
    [InlineData(24, 8)]
    [InlineData(16, 8)]
    [InlineData(12, 6)]
    [InlineData(10, 5)]
    [InlineData(7, 7)]
    [InlineData(5, 5)]
    [InlineData(13, 6)]
    public void UnitSizePrefersLargeDivisors(int size, int expected)
    {
        Assert.Equal((expected, expected, expected), ClassicSeeding.UnitSize(Settings(size, size, size, false)));
    }

    [Fact]
    public void UnitSettingsScaleBudgets()
    {
        var s = Settings(24, 24, 24, true);
        s.Limit[Cell] = 1382; // 10% of the volume
        var u = ClassicSeeding.UnitSettings(s, (8, 8, 8));
        Assert.False(u.SymX);
        Assert.Equal(51, u.Limit[Cell]); // floor(1382 * 512 / 13824)
        Assert.Equal(-1, u.Limit[Water]);
        Assert.Equal(0, u.Limit[Water + Active]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TiledRestartsRespectBudgetsAndSymmetryAndReproduce(bool sym)
    {
        var s = Settings(12, 12, 12, sym);
        s.Limit[Cell] = 300;
        s.Limit[Cryotheum] = 0;
        var pool = ClassicUnitPool.Build(s, ClassicUnitPool.CandidateSizes(s, new[] { 4, 6 }), 20_000, 7);
        Assert.Equal(2, pool.Units.Count);

        using var a = new ClassicOpt(s, useNet: false, seed: 3, unitPool: pool);
        using var b = new ClassicOpt(s, useNet: false, seed: 3, unitPool: ClassicUnitPool.Build(s, ClassicUnitPool.CandidateSizes(s, new[] { 4, 6 }), 20_000, 7));
        Assert.True(a.TiledRestarts);
        for (int i = 0; i < 30_000; ++i) { a.Step(); b.Step(); }
        Assert.Equal(a.Best.State.Data, b.Best.State.Data);

        var counts = ClassicExport.CountTiles(a.Best.State);
        Assert.True(counts[Cell] <= 300, $"cells {counts[Cell]}");
        Assert.Equal(0, counts[Cryotheum]);
        Assert.True(a.Best.Value.NetHeat <= 0);
        if (sym)
        {
            var g = a.Best.State;
            for (int x = 0; x < 12; ++x)
            for (int y = 0; y < 12; ++y)
            for (int z = 0; z < 12; ++z)
                Assert.Equal(g[x, y, z], g[11 - x, 11 - y, 11 - z]);
        }
        // The seeded run should not be worse than the unit's own density would suggest is trivially reachable.
        Assert.True(a.Best.Value.Breed > 0);
    }
}

public sealed class ClassicUnitPoolTests
{
    private static ClassicSettings Target()
    {
        var fuel = ClassicPresets.FindFuel("Default", "LEU-235")!;
        var s = new ClassicSettings { SizeX = 16, SizeY = 16, SizeZ = 16, FuelBasePower = fuel.BasePower, FuelBaseHeat = fuel.BaseHeat, EnsureHeatNeutral = true, Goal = ClassicGoal.Breeder };
        Array.Copy(ClassicPresets.FindCoolingRates("Default")!.Rates, s.CoolingRates, NumCoolerIds);
        Array.Fill(s.Limit, -1);
        for (int t = Active; t < Cell; ++t) s.Limit[t] = 0;
        return s;
    }

    [Fact]
    public void CandidateSizesClipToBoxAndDropTheBoxItself()
    {
        var s = Target();
        s.SizeX = 5; s.SizeY = 20; s.SizeZ = 20;
        var sizes = ClassicUnitPool.CandidateSizes(s, new[] { 4, 5, 6, 8 }).ToList();
        Assert.Equal(new[] { (4, 4, 4), (5, 5, 5), (5, 6, 6), (5, 8, 8) }, sizes);
        s.SizeY = 5; s.SizeZ = 5;
        Assert.Equal(new[] { (4, 4, 4) }, ClassicUnitPool.CandidateSizes(s, new[] { 4, 5, 6, 8 }).ToList());
    }

    [Fact]
    public void PicksEveryUnitOnceThenPrefersTheBestOutcome()
    {
        var s = Target();
        var pool = ClassicUnitPool.Build(s, ClassicUnitPool.CandidateSizes(s, new[] { 4, 5, 6 }), 5_000, 1);
        Assert.Equal(3, pool.Units.Count);
        Assert.All(pool.Units, u => Assert.True(u.Score > 0, "units must be feasible and non-empty"));
        var rng = new Rng(1);
        var first = new[] { pool.Pick(rng), pool.Pick(rng), pool.Pick(rng) };
        Assert.Equal(new[] { 0, 1, 2 }, first);
        pool.Report(0, 10); pool.Report(1, 30); pool.Report(2, 20);
        int picks1 = 0;
        for (int i = 0; i < 400; ++i) if (pool.Pick(rng) == 1) ++picks1;
        Assert.InRange(picks1, 300, 400); // ~75% greedy + a share of the 25% exploration
    }

    [Fact]
    public void AdoptionAddsCropsUpToCapacityThenReplacesTheWorstTriedUnit()
    {
        var s = Target();
        var pool = ClassicUnitPool.Build(s, ClassicUnitPool.CandidateSizes(s, new[] { 4 }), 5_000, 2);
        var rng = new Rng(3);
        var big = new Grid3(16, 16, 16, Air);
        for (int i = 0; i < big.Length; ++i) big.Data[i] = (i % 3 == 0) ? Cell : (i % 3 == 1) ? Cryotheum : Moderator;
        int added = 0;
        for (int k = 0; k < 20; ++k)
        {
            big.Data[rng.NextInt(big.Length - 1)] = Water; // make each crop differ
            if (pool.TryAdopt(0, big, rng)) ++added;
        }
        Assert.Equal(ClassicUnitPool.Capacity - 1, added); // fills up, then nobody has 3 uses yet -> no replacement
        Assert.Equal(ClassicUnitPool.Capacity, pool.Units.Count);
        for (int i = 1; i < pool.Units.Count; ++i) { pool.Report(i, i); pool.Report(i, i); pool.Report(i, i); }
        big.Data[0] = Redstone;
        Assert.True(pool.TryAdopt(0, big, rng));
        Assert.Equal(ClassicUnitPool.Capacity, pool.Units.Count);
        Assert.DoesNotContain(pool.Units, u => u.Uses == 3 && u.MeanOutcome == 1); // the worst (mean 1) was replaced
    }
}
