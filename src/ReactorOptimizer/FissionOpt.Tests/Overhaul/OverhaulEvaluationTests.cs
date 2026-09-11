using FissionOpt.Core;
using FissionOpt.Core.Overhaul;
using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Tests.Overhaul;

/// <summary>Hand-checked overhaul cases that need no C++ toolchain.</summary>
public sealed class OverhaulEvaluationTests
{
    private static OverhaulSettings Settings(int sx, int sy, int sz, params OverhaulFuel[] fuels)
    {
        var s = new OverhaulSettings { SizeX = sx, SizeY = sy, SizeZ = sz };
        s.Fuels.AddRange(fuels);
        Array.Fill(s.Limits, -1);
        Array.Fill(s.SourceLimits, -1);
        s.Compute();
        return s;
    }

    [Fact]
    public void CellTypesExpandPerNeutronSourceUnlessSelfPriming()
    {
        var s = Settings(1, 1, 1, new OverhaulFuel(1.75, -1, 60, 540, false), new OverhaulFuel(1.8, -1, 30, 1620, true));
        Assert.Equal(new[] { (0, 0), (0, 1), (0, 2), (0, 3), (1, 0) }, s.CellTypes);
        Assert.Equal(1620 * 1.8, s.MaxOutput);
        Assert.Equal(30, s.MinCriticality);
        Assert.Equal(540, s.MinHeat);
    }

    [Fact]
    public void TwoSelfPrimingCellsThroughOneModeratorActivateEachOther()
    {
        // LEU-235 self-priming-ish fuel with a low criticality so a single M0 (flux 10) suffices.
        var s = Settings(3, 1, 1, new OverhaulFuel(1.75, -1, 10, 540, true));
        var g = new Grid3(3, 1, 1, Air);
        g[0, 0, 0] = C0; g[1, 0, 0] = M0; g[2, 0, 0] = C0;
        var e = new OverhaulEvaluation();
        e.Initialize(s, false);
        e.Run(g);
        Assert.Equal(2, e.NActiveCells);
        Assert.Equal(20, e.TotalRawFlux);
        Assert.Equal(10, e.MaxCellFlux);
        Assert.True(e.IsFunctionalAt(e.Index(1, 0, 0)));
        Assert.True(e.IsActiveAt(e.Index(1, 0, 0)));
        // Moderators do not join clusters, so each cell is its own (casing-connected) cluster.
        Assert.Equal(2, e.ClusterCount);
        Assert.True(e.GetCluster(0).HasCasingConnection);
        Assert.Equal(540, e.GetCluster(0).Heat); // heatMult 1 per cell
        Assert.Equal(2 * 540, e.TotalPositiveNetHeat); // no cooling at all
        Assert.Equal(3.0 / 3.0, e.Density);
        Assert.Equal(1.0, e.SparsityPenalty);
    }

    [Fact]
    public void NonSelfPrimingCellWithoutSourceNeverStarts()
    {
        var s = Settings(3, 1, 1, new OverhaulFuel(1.75, -1, 10, 540, false));
        var g = new Grid3(3, 1, 1, Air);
        g[0, 0, 0] = C0; g[1, 0, 0] = M0; g[2, 0, 0] = C0; // source 0 on both
        var e = new OverhaulEvaluation();
        e.Initialize(s, false);
        e.Run(g);
        Assert.Equal(0, e.NActiveCells);
        var canon = g.Clone();
        e.Canonicalize(canon);
        Assert.All(canon.Data, t => Assert.Equal(Air, t));

        g[0, 0, 0] = C0 + 1; // give one cell a neutron source: it primes, and its flux lights the other
        e.Run(g);
        Assert.Equal(2, e.NActiveCells);
    }

    [Fact]
    public void LongChainDoesNotOverflowStack()
    {
        // 24³ checkerboard of cells; the other parity is a moderator on even-y planes (so every cell
        // gets flux) and a conductor on odd-y planes (so every cell joins one cluster). Thousands of
        // cells in one flux graph and one cluster: the C++ recursion would be thousands of frames deep.
        var s = Settings(24, 24, 24, new OverhaulFuel(1.0, -1, 10, 100, true));
        var g = new Grid3(24, 24, 24, Air);
        for (int x = 0; x < 24; ++x)
        for (int y = 0; y < 24; ++y)
        for (int z = 0; z < 24; ++z)
            g[x, y, z] = ((x + y + z) & 1) == 0 ? C0 : (y & 1) == 0 ? M0 : Conductor;
        var e = new OverhaulEvaluation();
        e.Initialize(s, false);
        e.Run(g);
        Assert.Equal(24 * 24 * 24 / 2, e.NActiveCells);
        Assert.Equal(1, e.ClusterCount);
        Assert.Equal(24 * 24 * 24 / 2 + 24 * 12 * 24 / 2, e.GetCluster(0).Tiles.Count);
    }
}
