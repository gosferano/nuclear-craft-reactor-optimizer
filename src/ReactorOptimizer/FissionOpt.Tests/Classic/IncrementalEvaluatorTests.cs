using FissionOpt.Core;
using FissionOpt.Core.Classic;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>
/// The incremental evaluator must agree with the scalar reference after every mutation batch and
/// after every undo: all per-tile intermediates exactly, the totals to 1e-9 relative.
/// </summary>
public sealed class IncrementalEvaluatorTests
{
    private static ClassicSettings RandomSettings(Random rng, int maxDim)
    {
        var s = new ClassicSettings
        {
            SizeX = rng.Next(1, maxDim + 1), SizeY = rng.Next(1, maxDim + 1), SizeZ = rng.Next(1, maxDim + 1),
            FuelBasePower = 120, FuelBaseHeat = rng.Next(5) == 0 ? 0 : 50,
            EnsureActiveCoolerAccessible = rng.Next(2) == 0,
        };
        Array.Fill(s.Limit, -1);
        if (s.EnsureActiveCoolerAccessible)
            for (int t = Active; t < Cell; ++t) s.Limit[t] = 0; // the supported combination
        for (int i = 0; i < s.CoolingRates.Length; ++i) s.CoolingRates[i] = rng.Next(1, 400) + (rng.Next(4) == 0 ? 0.25 : 0);
        return s;
    }

    private static int RandomTile(Random rng, ClassicSettings s)
    {
        while (true)
        {
            int t = rng.Next(100) switch { < 25 => Air, < 45 => Cell, < 60 => Moderator, _ => rng.Next(Cell) };
            if (t < Cell && s.Limit[t] == 0) continue;
            return t;
        }
    }

    private static void AssertAgrees(IncrementalClassicEvaluator inc, ClassicSettings s, Grid3 state, string when)
    {
        var scalar = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(state, scalar);
        var e = new ClassicEvaluation();
        inc.WriteTo(e, withInvalidList: true);
        Assert.True(scalar.Breed == e.Breed, $"{when}: breed {scalar.Breed} vs {e.Breed}");
        Assert.Equal(scalar.PowerMult, e.PowerMult, 1e-9 * Math.Max(1, Math.Abs(scalar.PowerMult)));
        Assert.Equal(scalar.HeatMult, e.HeatMult, 1e-9 * Math.Max(1, Math.Abs(scalar.HeatMult)));
        Assert.Equal(scalar.Cooling, e.Cooling, 1e-9 * Math.Max(1, Math.Abs(scalar.Cooling)));
        var a = scalar.InvalidTiles.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToList();
        var b = e.InvalidTiles.OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToList();
        Assert.True(a.SequenceEqual(b), $"{when}: invalid tiles differ: scalar {a.Count} vs incremental {b.Count}");
        Assert.Equal(scalar.InvalidTiles.Count, inc.InvalidCount);
        // Per-tile intermediates via an independent scalar recomputation of the observable bits.
        var counts = new int[Air + 1];
        foreach (var t in state.Data) ++counts[t];
        Assert.Equal(counts, inc.CountByTile.ToArray());
        var invalidByTile = new int[Air + 1];
        foreach (var c in scalar.InvalidTiles) ++invalidByTile[state[c]];
        Assert.Equal(invalidByTile, inc.InvalidByTile.ToArray());
    }

    [Fact]
    public void RandomMutationSequencesMatchScalarEvaluator()
    {
        var rng = new Random(20200815);
        int sequences = 300, batchesPerSequence = 150;
        for (int q = 0; q < sequences; ++q)
        {
            var s = RandomSettings(rng, 9);
            var state = new Grid3(s.SizeX, s.SizeY, s.SizeZ);
            for (int i = 0; i < state.Length; ++i) state.Data[i] = RandomTile(rng, s);
            var inc = new IncrementalClassicEvaluator(s, state);
            AssertAgrees(inc, s, state, $"seq {q} rebuild");
            var before = state.Clone();
            for (int b = 0; b < batchesPerSequence; ++b)
            {
                before.CopyFrom(state);
                int n = rng.Next(1, 9);
                for (int k = 0; k < n; ++k)
                    inc.Set(rng.Next(s.SizeX), rng.Next(s.SizeY), rng.Next(s.SizeZ), RandomTile(rng, s));
                inc.Apply();
                AssertAgrees(inc, s, state, $"seq {q} batch {b} apply");
                if (rng.Next(3) == 0)
                {
                    inc.Undo();
                    Assert.Equal(before.Data, state.Data);
                    AssertAgrees(inc, s, state, $"seq {q} batch {b} undo");
                }
                else
                {
                    inc.Commit();
                }
            }
        }
    }

    [Fact]
    public void SupportsRefusesAccessibilityWithActiveCoolers()
    {
        var s = new ClassicSettings { SizeX = 2, SizeY = 2, SizeZ = 2, EnsureActiveCoolerAccessible = true };
        Array.Fill(s.Limit, 0);
        Assert.True(IncrementalClassicEvaluator.Supports(s));
        s.Limit[Water + Active] = -1;
        Assert.False(IncrementalClassicEvaluator.Supports(s));
        s.EnsureActiveCoolerAccessible = false;
        Assert.True(IncrementalClassicEvaluator.Supports(s));
    }
}
