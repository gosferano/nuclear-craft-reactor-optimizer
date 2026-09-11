using FissionOpt.Core;
using FissionOpt.Core.Overhaul;
using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Tests.Overhaul;

/// <summary>Random (settings, shieldOn, state) generator for the overhaul differential test.</summary>
public static class OverhaulFuzz
{
    public const int MaxDim = 12;

    public static (OverhaulSettings settings, bool shieldOn, Grid3 state) Next(Random rng)
    {
        var s = new OverhaulSettings
        {
            SizeX = rng.Next(1, MaxDim + 1),
            SizeY = rng.Next(1, MaxDim + 1),
            SizeZ = rng.Next(1, MaxDim + 1),
            Goal = (OverhaulGoal)rng.Next(4),
            Controllable = rng.Next(2) == 0,
            SymX = rng.Next(2) == 0, SymY = rng.Next(2) == 0, SymZ = rng.Next(2) == 0,
        };
        int nFuels = rng.Next(1, 4);
        for (int i = 0; i < nFuels; ++i)
        {
            s.Fuels.Add(new OverhaulFuel(
                efficiency: Math.Round(1.0 + rng.NextDouble(), 2),
                limit: rng.Next(3) == 0 ? rng.Next(0, 30) : -1,
                // Low criticalities make flux activation reachable on small random grids.
                criticality: rng.Next(4) == 0 ? rng.Next(1, 30) : rng.Next(10, 200),
                heat: rng.Next(20, 2000),
                selfPriming: rng.Next(4) == 0));
        }
        for (int i = 0; i < s.Limits.Length; ++i) s.Limits[i] = rng.Next(4) == 0 ? rng.Next(0, 64) : -1;
        for (int i = 0; i < 3; ++i) s.SourceLimits[i] = rng.Next(3) == 0 ? rng.Next(0, 10) : -1;
        s.Compute();

        bool shieldOn = rng.Next(3) == 0;
        var state = new Grid3(s.SizeX, s.SizeY, s.SizeZ);
        Fill(rng, state, s.CellTypes.Count);
        return (s, shieldOn, state);
    }

    private static void Fill(Random rng, Grid3 state, int nCellTypes)
    {
        var data = state.Data;
        int maxId = C0 + nCellTypes;
        switch (rng.Next(4))
        {
            case 0:
                for (int i = 0; i < data.Length; ++i) data[i] = rng.Next(maxId);
                break;
            case 1:
            {
                // Reactor-like: cells and moderators dominate, a few sink types, some reflectors/shields.
                int[] sinks = Palette(rng, rng.Next(1, 6));
                for (int i = 0; i < data.Length; ++i)
                {
                    int r = rng.Next(100);
                    data[i] = r < 8 ? Air
                        : r < 30 ? C0 + rng.Next(nCellTypes)
                        : r < 55 ? M0 + rng.Next(3)
                        : r < 60 ? R0 + rng.Next(2)
                        : r < 63 ? Shield
                        : r < 65 ? Irradiator
                        : r < 68 ? Conductor
                        : sinks[rng.Next(sinks.Length)];
                }
                break;
            }
            case 2:
            {
                // Lines: cells separated by 1–4 moderators along x, so flux chains and reflectors trigger.
                int[] sinks = Palette(rng, rng.Next(1, 4));
                for (int i = 0; i < data.Length; ++i)
                {
                    var c = new Coord(i / (state.SizeY * state.SizeZ), i / state.SizeZ % state.SizeY, i % state.SizeZ);
                    int period = 2 + (c.Y + c.Z) % 4;
                    if (c.X % period == 0) data[i] = rng.Next(5) == 0 ? R0 + rng.Next(2) : C0 + rng.Next(nCellTypes);
                    else if (rng.Next(6) == 0) data[i] = sinks[rng.Next(sinks.Length)];
                    else if (rng.Next(10) == 0) data[i] = Shield;
                    else data[i] = M0 + rng.Next(3);
                }
                break;
            }
            default:
            {
                int[] sinks = Palette(rng, rng.Next(2, 8));
                for (int i = 0; i < data.Length; ++i)
                {
                    int r = rng.Next(100);
                    data[i] = r < 40 ? Air
                        : r < 52 ? C0 + rng.Next(nCellTypes)
                        : r < 64 ? M0 + rng.Next(3)
                        : r < 68 ? R0 + rng.Next(2)
                        : r < 70 ? Irradiator
                        : r < 74 ? Conductor
                        : sinks[rng.Next(sinks.Length)];
                }
                break;
            }
        }
    }

    private static int[] Palette(Random rng, int n)
    {
        var p = new int[n];
        for (int i = 0; i < n; ++i) p[i] = rng.Next(NumHeatSinks);
        return p;
    }
}
