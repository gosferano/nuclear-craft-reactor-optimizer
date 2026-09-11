using FissionOpt.Core;
using FissionOpt.Core.Classic;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tests.Classic;

/// <summary>
/// Random (settings, state) generator for the classic differential test. Mixes several tile
/// distributions so that cooler activation chains (Water → Gold → Iron, Lapis → Tin, …) and
/// active-cooler accessibility actually get exercised rather than drowned in noise.
/// </summary>
public static class ClassicFuzz
{
    public const int MaxDim = 12;

    public static (ClassicSettings settings, Grid3 state) Next(Random rng)
    {
        var s = new ClassicSettings
        {
            SizeX = rng.Next(1, MaxDim + 1),
            SizeY = rng.Next(1, MaxDim + 1),
            SizeZ = rng.Next(1, MaxDim + 1),
            FuelBasePower = Math.Round(rng.NextDouble() * 1000 + 1, 2),
            FuelBaseHeat = rng.Next(10) == 0 ? 0.0 : Math.Round(rng.NextDouble() * 2000 + 1, 2),
            EnsureActiveCoolerAccessible = rng.Next(2) == 0,
            EnsureHeatNeutral = rng.Next(2) == 0,
            Goal = (ClassicGoal)rng.Next(3),
            SymX = rng.Next(2) == 0,
            SymY = rng.Next(2) == 0,
            SymZ = rng.Next(2) == 0,
        };
        for (int i = 0; i < s.Limit.Length; ++i) s.Limit[i] = rng.Next(4) == 0 ? rng.Next(0, 64) : -1;
        for (int i = 0; i < s.CoolingRates.Length; ++i) s.CoolingRates[i] = rng.Next(1, 500) + (rng.Next(3) == 0 ? 0.5 : 0.0);

        var state = new Grid3(s.SizeX, s.SizeY, s.SizeZ);
        FillState(rng, state);
        return (s, state);
    }

    private static void FillState(Random rng, Grid3 state)
    {
        var data = state.Data;
        switch (rng.Next(4))
        {
            case 0:
                // Uniform over every tile ID.
                for (int i = 0; i < data.Length; ++i) data[i] = rng.Next(Air + 1);
                break;
            case 1:
            {
                // Dense reactor-ish mix: lots of cells and moderators, a handful of cooler types.
                int[] palette = RandomCoolerPalette(rng, rng.Next(1, 6));
                for (int i = 0; i < data.Length; ++i)
                {
                    int r = rng.Next(100);
                    data[i] = r < 20 ? Air
                        : r < 40 ? Cell
                        : r < 55 ? Moderator
                        : palette[rng.Next(palette.Length)];
                }
                break;
            }
            case 2:
            {
                // Chain-heavy: the cooler types that depend on each other, both passive and active.
                int[] chain = { Water, Redstone, Gold, Iron, Quartz, Diamond, Glowstone, Copper, Lapis, Tin, Helium, Emerald, Magnesium, Enderium, Cryotheum };
                for (int i = 0; i < data.Length; ++i)
                {
                    int r = rng.Next(100);
                    if (r < 15) data[i] = Air;
                    else if (r < 30) data[i] = Cell;
                    else if (r < 40) data[i] = Moderator;
                    else
                    {
                        int c = chain[rng.Next(chain.Length)];
                        data[i] = rng.Next(3) == 0 ? c + Active : c;
                    }
                }
                break;
            }
            default:
            {
                // Sparse: mostly air, so accessibility paths through air get exercised.
                int[] palette = RandomCoolerPalette(rng, rng.Next(1, 4));
                for (int i = 0; i < data.Length; ++i)
                {
                    int r = rng.Next(100);
                    data[i] = r < 55 ? Air
                        : r < 65 ? Cell
                        : r < 75 ? Moderator
                        : palette[rng.Next(palette.Length)];
                }
                break;
            }
        }
    }

    private static int[] RandomCoolerPalette(Random rng, int n)
    {
        var p = new int[n];
        for (int i = 0; i < n; ++i)
        {
            int type = rng.Next(Active);
            p[i] = rng.Next(2) == 0 ? type + Active : type;
        }
        return p;
    }
}
