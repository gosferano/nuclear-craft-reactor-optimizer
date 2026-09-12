using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Tiling-seeded restarts (not part of upstream FissionOpt). Every rule of the classic evaluator is
/// local, so a good design for a small box, tiled, is a far better starting point for a large box
/// than a random grid; the ordinary hill-climber then repairs the seams and optimizes the walls.
/// </summary>
public static class ClassicSeeding
{
    /// <summary>
    /// Tiled restarts are opt-in. They win short runs by a wide margin (24³ breeding: ~7 000 cells in seconds vs
    /// hours for random restarts) but were observed to plateau there in hours-long runs, where upstream's random
    /// restarts kept creeping past them (7 200). Until that is resolved, "auto" means upstream behaviour.
    /// </summary>
    public const bool AutoTiled = false;
    /// <summary>Default number of steps spent optimizing each unit design (units run in parallel; 4³ needs ~2M to reach the checkerboard density).</summary>
    public const int DefaultUnitSteps = 1_500_000;

    /// <summary>Resolves the restart mode: null = automatic by grid volume.</summary>
    public static bool UseTiled(ClassicSettings target, bool? requested)
    {
        if (requested.HasValue) return requested.Value;
        return AutoTiled;
    }

    /// <summary>Default candidate unit sizes. 2 and 3 matter: short-period lattices are found instantly there and never on 6³+.</summary>
    public static readonly int[] DefaultUnitSizes = { 2, 3, 4, 5, 6, 8 };
    /// <summary>Unit fitness tie-break towards cooling surplus (bounded by this value; a cell is worth 1).</summary>
    public const double UnitSurplusTieBreak = 0.05;

    /// <summary>Builds the unit pool for <paramref name="target"/>, or returns null when tiled restarts are not wanted.</summary>
    public static ClassicUnitPool? PoolFor(ClassicSettings target, bool? requested, int seed, IEnumerable<int>? unitSizes = null, int unitSteps = DefaultUnitSteps)
    {
        if (!UseTiled(target, requested)) return null;
        var sizes = ClassicUnitPool.CandidateSizes(target, unitSizes ?? DefaultUnitSizes).ToList();
        if (sizes.Count == 0) return null;
        return ClassicUnitPool.Build(target, sizes, unitSteps, seed);
    }

    /// <summary>Unit box for a target: per axis, the largest divisor in 4..8, else min(size, 6).</summary>
    public static (int x, int y, int z) UnitSize(ClassicSettings target) => (Unit(target.SizeX), Unit(target.SizeY), Unit(target.SizeZ));

    private static int Unit(int size)
    {
        for (int u = 8; u >= 4; --u)
            if (u <= size && size % u == 0)
                return u;
        return Math.Min(size, 6);
    }

    /// <summary>
    /// Settings for the unit optimization: same fuel, rates, goal and constraints; no symmetry;
    /// per-block budgets scaled down in proportion to the volume; and <b>periodic</b>, so the unit is
    /// optimized in the neighbourhood it will actually have — surrounded by copies of itself. Casing-only
    /// coolers are then invalid by their own rule, and a cell may see its own copy through the wrap.
    /// </summary>
    public static ClassicSettings UnitSettings(ClassicSettings target, (int x, int y, int z) unit)
    {
        var s = target.Clone();
        s.SizeX = unit.x; s.SizeY = unit.y; s.SizeZ = unit.z;
        s.SymX = s.SymY = s.SymZ = false;
        s.Periodic = true;
        s.SurplusTieBreak = UnitSurplusTieBreak;
        double scale = (double)s.Volume / target.Volume;
        for (int t = 0; t < s.Limit.Length; ++t)
            if (target.Limit[t] >= 0)
                s.Limit[t] = (int)Math.Floor(target.Limit[t] * scale);
        return s;
    }

    /// <summary>Optimizes a unit design for <paramref name="steps"/> steps (random restarts, no value net) and returns its best state.</summary>
    public static Grid3 OptimizeUnit(ClassicSettings target, (int x, int y, int z) unit, int steps, int seed)
    {
        var s = UnitSettings(target, unit);
        using var opt = new ClassicOpt(s, useNet: false, seed: unchecked(seed * 31 + 0x5EED));
        for (int i = 0; i < steps; ++i)
            opt.Step();
        return opt.Best.State.Clone();
    }
}
