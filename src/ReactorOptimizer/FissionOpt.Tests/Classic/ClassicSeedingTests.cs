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
        var unit = ClassicSeeding.UnitSize(s);
        var pattern = ClassicSeeding.OptimizeUnit(s, unit, 20_000, 7);
        Assert.Equal((6, 6, 6), unit);

        using var a = new ClassicOpt(s, useNet: false, seed: 3, tilePattern: pattern);
        using var b = new ClassicOpt(s, useNet: false, seed: 3, tilePattern: pattern);
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
