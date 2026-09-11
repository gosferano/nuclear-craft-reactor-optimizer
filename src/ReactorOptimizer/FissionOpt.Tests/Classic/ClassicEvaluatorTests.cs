using FissionOpt.Core;
using FissionOpt.Core.Classic;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>
/// Hand-checked cases that need no C++ toolchain. The differential fuzz test is the real
/// acceptance test; these exist so a broken port fails fast on any machine.
/// </summary>
public sealed class ClassicEvaluatorTests
{
    private static ClassicSettings Settings3(double power = 120, double heat = 50)
    {
        var s = new ClassicSettings { SizeX = 3, SizeY = 3, SizeZ = 3, FuelBasePower = power, FuelBaseHeat = heat };
        Array.Fill(s.Limit, -1);
        for (int i = 0; i < s.CoolingRates.Length; ++i) s.CoolingRates[i] = 60 + i;
        s.CoolingRates[Water] = 60;
        s.CoolingRates[Water + Active] = 320;
        return s;
    }

    [Fact]
    public void CenterCellWithModeratorsAndWater()
    {
        // Expected values were produced by the C++ oracle for this exact grid.
        var s = Settings3();
        var g = new Grid3(3, 3, 3, Air);
        g[1, 1, 1] = Cell;
        g[0, 1, 1] = Moderator; g[2, 1, 1] = Moderator;
        foreach (var (x, y, z) in new[] { (0, 0, 1), (0, 1, 0), (0, 1, 2), (0, 2, 1), (2, 0, 1), (2, 1, 0), (2, 1, 2), (2, 2, 1),
                                          (1, 0, 0), (1, 0, 2), (1, 2, 0), (1, 2, 2) })
            g[x, y, z] = Water;
        g[1, 2, 1] = Water + Active;

        var e = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(g, e);

        Assert.Equal(1, e.Breed);
        Assert.Equal(1 + 2 * (1.0 / 6.0), e.PowerMult, 1e-12);
        Assert.Equal(1 + 2 * (2.0 / 6.0), e.HeatMult, 1e-12);
        Assert.Equal(800.0, e.Cooling); // 8 waters next to active moderators + 1 active water next to the cell
        Assert.Equal(new[] { new Coord(1, 0, 0), new Coord(1, 0, 2), new Coord(1, 2, 0), new Coord(1, 2, 2) }, e.InvalidTiles);
        Assert.Equal(1.0, e.DutyCycle);
        Assert.Equal(160.0, e.Power, 1e-9);
    }

    [Fact]
    public void NoCellsGivesUnitDutyCycleNotNaN()
    {
        // heat == 0 makes cooling/heat NaN or +inf; std::min(1.0, x) returns 1.0 in both cases.
        var s = Settings3();
        var g = new Grid3(3, 3, 3, Air);
        var e = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(g, e);
        Assert.Equal(0.0, e.Heat);
        Assert.Equal(1.0, e.DutyCycle);
        Assert.Equal(1.0, e.Efficiency);

        g[0, 0, 0] = Water; // one cooler, no cell: cooling > 0, heat == 0 → +inf ratio
        g[0, 0, 1] = Cell;
        s.FuelBaseHeat = 0;
        new ClassicEvaluator(s).Run(g, e);
        Assert.Equal(1.0, e.DutyCycle);
    }

    [Fact]
    public void InaccessibleActiveCoolerIsInvalidOnlyWhenEnforced()
    {
        var s = Settings3();
        var g = new Grid3(3, 3, 3, Water); // fully enclosed by passive water
        g[1, 1, 1] = Water + Active;
        g[1, 1, 0] = Cell;
        var e = new ClassicEvaluation();

        s.EnsureActiveCoolerAccessible = false;
        new ClassicEvaluator(s).Run(g, e);
        Assert.DoesNotContain(new Coord(1, 1, 1), e.InvalidTiles);

        s.EnsureActiveCoolerAccessible = true;
        new ClassicEvaluator(s).Run(g, e);
        Assert.Contains(new Coord(1, 1, 1), e.InvalidTiles);

        g[1, 1, 2] = Air; // open a path to the casing
        new ClassicEvaluator(s).Run(g, e);
        Assert.DoesNotContain(new Coord(1, 1, 1), e.InvalidTiles);
    }

    [Fact]
    public void LargeGridDoesNotOverflowStack()
    {
        // 24³ all-air except one active cooler and a cell: the C++ recursion would be ~13.8k frames deep.
        var s = new ClassicSettings { SizeX = 24, SizeY = 24, SizeZ = 24, FuelBasePower = 1, FuelBaseHeat = 1, EnsureActiveCoolerAccessible = true };
        Array.Fill(s.Limit, -1);
        Array.Fill(s.CoolingRates, 1.0);
        var g = new Grid3(24, 24, 24, Air);
        g[12, 12, 12] = Water + Active;
        g[12, 12, 13] = Cell;
        var e = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(g, e);
        Assert.Equal(1.0, e.Cooling);
        Assert.Empty(e.InvalidTiles);
    }
}
