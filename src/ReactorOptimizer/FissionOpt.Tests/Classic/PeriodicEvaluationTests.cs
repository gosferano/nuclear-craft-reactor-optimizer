using FissionOpt.Core;
using FissionOpt.Core.Classic;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>
/// The torus is only a modelling device; its meaning is "this unit surrounded by copies of itself".
/// So a unit evaluated periodically must agree, tile for tile, with the ordinary evaluator's view of
/// the centre copy when the unit is tiled far enough from any wall that no rule can see the casing.
/// </summary>
public sealed class PeriodicEvaluationTests
{
    private const int Reach = 9; // longest rule dependency: iron <- gold <- water <- moderator <- cell ray (5)

    private static ClassicSettings Settings(int x, int y, int z, Random rng, bool periodic)
    {
        var s = new ClassicSettings { SizeX = x, SizeY = y, SizeZ = z, FuelBasePower = 120, FuelBaseHeat = 50, EnsureActiveCoolerAccessible = false, Periodic = periodic };
        Array.Fill(s.Limit, -1);
        for (int i = 0; i < s.CoolingRates.Length; ++i) s.CoolingRates[i] = 10 + i;
        return s;
    }

    [Fact]
    public void PeriodicUnitMatchesTheCentreOfItsOwnTiling()
    {
        var rng = new Random(2024);
        for (int q = 0; q < 300; ++q)
        {
            int ux = rng.Next(1, 7), uy = rng.Next(1, 7), uz = rng.Next(1, 7);
            var unit = new Grid3(ux, uy, uz);
            for (int i = 0; i < unit.Length; ++i)
                unit.Data[i] = rng.Next(100) switch { < 25 => Air, < 50 => Cell, < 65 => Moderator, _ => rng.Next(Cell) };

            var torus = new IncrementalClassicEvaluator(Settings(ux, uy, uz, rng, periodic: true), unit);

            int kx = 2 * ((Reach + ux - 1) / ux) + 1, ky = 2 * ((Reach + uy - 1) / uy) + 1, kz = 2 * ((Reach + uz - 1) / uz) + 1;
            var big = new Grid3(ux * kx, uy * ky, uz * kz);
            for (int x = 0; x < big.SizeX; ++x)
            for (int y = 0; y < big.SizeY; ++y)
            for (int z = 0; z < big.SizeZ; ++z)
                big[x, y, z] = unit[x % ux, y % uy, z % uz];
            var flat = new IncrementalClassicEvaluator(Settings(big.SizeX, big.SizeY, big.SizeZ, rng, periodic: false), big);

            int ox = ux * (kx / 2), oy = uy * (ky / 2), oz = uz * (kz / 2);
            for (int x = 0; x < ux; ++x)
            for (int y = 0; y < uy; ++y)
            for (int z = 0; z < uz; ++z)
            {
                int ti = unit.Index(x, y, z), bi = big.Index(ox + x, oy + y, oz + z);
                string where = $"unit {ux}x{uy}x{uz} case {q} tile ({x},{y},{z}) = {unit[x, y, z]}";
                Assert.True(torus.MultAt(ti) == flat.MultAt(bi), $"{where}: mult {torus.MultAt(ti)} vs {flat.MultAt(bi)}");
                Assert.True(torus.IsActiveAt(ti) == flat.IsActiveAt(bi), $"{where}: active {torus.IsActiveAt(ti)} vs {flat.IsActiveAt(bi)}");
                Assert.True(torus.IsInvalidAt(ti) == flat.IsInvalidAt(bi), $"{where}: invalid {torus.IsInvalidAt(ti)} vs {flat.IsInvalidAt(bi)}");
                Assert.True(torus.IsModeratorInLineAt(ti) == flat.IsModeratorInLineAt(bi), $"{where}: inLine");
            }
        }
    }

    [Fact]
    public void CasingCoolersAreInvalidEverywhereOnATorus()
    {
        var rng = new Random(5);
        var s = Settings(4, 4, 4, rng, periodic: true);
        var g = new Grid3(4, 4, 4, Cell);
        g[0, 0, 0] = Lapis; g[1, 1, 1] = Helium; g[2, 2, 2] = Magnesium; g[3, 3, 3] = Enderium; g[0, 1, 2] = Cryotheum;
        var e = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(g, e);
        var invalid = e.InvalidTiles.ToHashSet();
        Assert.Contains(new Coord(0, 0, 0), invalid);
        Assert.Contains(new Coord(1, 1, 1), invalid);
        Assert.Contains(new Coord(2, 2, 2), invalid);
        Assert.Contains(new Coord(3, 3, 3), invalid);
        Assert.DoesNotContain(new Coord(0, 1, 2), invalid); // cryotheum: >= 2 adjacent cells, no casing needed
    }

    [Fact]
    public void ACellSeesItsOwnCopyThroughTheWrap()
    {
        // 1x1x3 torus: cell, moderator, moderator. Along z the ray goes mod, mod, then wraps to the cell
        // itself; along x and y the immediate neighbour is the cell itself.
        var rng = new Random(6);
        var s = Settings(1, 1, 3, rng, periodic: true);
        var g = new Grid3(1, 1, 3, Moderator);
        g[0, 0, 0] = Cell;
        var e = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(g, e);
        // Cell multiplier: 1 + x (2 dirs, itself) + y (2 dirs, itself) + z (2 dirs, through the moderators) = 7.
        // Each moderator has the cell (mult 7) as exactly one neighbour and itself/the other moderator otherwise.
        Assert.Equal(1, e.Breed);
        Assert.Equal(7.0 + 14 * (ModPower / 6.0), e.PowerMult, 1e-9);
        Assert.Equal(28.0 + 14 * (ModHeat / 6.0), e.HeatMult, 1e-9);
        Assert.Empty(e.InvalidTiles);
    }
}
