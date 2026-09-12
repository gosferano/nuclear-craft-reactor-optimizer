using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Tiling-seeded restarts (not part of upstream FissionOpt). Every rule of the classic evaluator is
/// local, so a good design for a small box, tiled, is a far better starting point for a large box
/// than a random grid; the ordinary hill-climber then repairs the seams and optimizes the walls.
/// </summary>
public static class ClassicSeeding
{
    /// <summary>Grids at least this large default to tiled restarts; below it random restarts explore better (measured: no gain at 10³, +2–3% at 24³).</summary>
    public const int AutoThreshold = 2000;
    /// <summary>Default number of steps spent optimizing each unit design (about a second each; units run in parallel).</summary>
    public const int DefaultUnitSteps = 200_000;

    /// <summary>Resolves the restart mode: null = automatic by grid volume.</summary>
    public static bool UseTiled(ClassicSettings target, bool? requested)
    {
        if (requested.HasValue) return requested.Value;
        var u = UnitSize(target);
        return target.Volume >= AutoThreshold && u.x * u.y * u.z < target.Volume;
    }

    /// <summary>Default candidate unit sizes.</summary>
    public static readonly int[] DefaultUnitSizes = { 4, 5, 6, 7, 8 };

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
    /// per-block budgets scaled down in proportion to the volume.
    /// </summary>
    public static ClassicSettings UnitSettings(ClassicSettings target, (int x, int y, int z) unit)
    {
        var s = target.Clone();
        s.SizeX = unit.x; s.SizeY = unit.y; s.SizeZ = unit.z;
        s.SymX = s.SymY = s.SymZ = false;
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
