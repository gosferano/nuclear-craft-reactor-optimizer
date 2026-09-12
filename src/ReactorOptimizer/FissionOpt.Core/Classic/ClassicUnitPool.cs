using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// A pool of unit designs for tiled restarts (see <see cref="ClassicSeeding"/>). Units of several
/// sizes are optimized up front; each episode picks one (every unit once, then epsilon-greedy on the
/// outcomes of the episodes it seeded); and when an episode converges, a unit-sized crop from the
/// interior of the converged design can replace a weaker pool member of the same size, so patterns
/// refined in the big box flow back into later restarts.
/// </summary>
public sealed class ClassicUnitPool
{
    public sealed class Unit
    {
        public required Grid3 Grid { get; init; }
        /// <summary>Per-block fitness of the unit on its own box (the selection heuristic before any episode feedback).</summary>
        public double Score { get; set; }
        public int Uses { get; set; }
        public double OutcomeSum { get; set; }
        public double BestOutcome { get; set; }
        public int Adopted { get; set; }
        public double MeanOutcome => Uses == 0 ? double.NaN : OutcomeSum / Uses;
        public (int x, int y, int z) Size => (Grid.SizeX, Grid.SizeY, Grid.SizeZ);
    }

    public const double Epsilon = 0.25;
    private readonly ClassicSettings _target;
    private readonly List<Unit> _units = new();

    public IReadOnlyList<Unit> Units => _units;

    private ClassicUnitPool(ClassicSettings target) => _target = target;

    /// <summary>Candidate cubic unit sizes, clipped per axis to the box. Default 4..8.</summary>
    public static IEnumerable<(int x, int y, int z)> CandidateSizes(ClassicSettings target, IEnumerable<int> sizes)
    {
        var seen = new HashSet<(int, int, int)>();
        foreach (int u in sizes)
        {
            var s = (Math.Min(u, target.SizeX), Math.Min(u, target.SizeY), Math.Min(u, target.SizeZ));
            if (s.Item1 * s.Item2 * s.Item3 < target.Volume && seen.Add(s))
                yield return s;
        }
    }

    /// <summary>Optimizes one unit per candidate size (in parallel; each is deterministic for the seed) and scores it.</summary>
    public static ClassicUnitPool Build(ClassicSettings target, IEnumerable<(int x, int y, int z)> sizes, int stepsPerUnit, int seed)
    {
        var pool = new ClassicUnitPool(target);
        var list = sizes.ToList();
        var grids = new Grid3[list.Count];
        Parallel.For(0, list.Count, k =>
        {
            grids[k] = ClassicSeeding.OptimizeUnit(target, list[k], stepsPerUnit, unchecked(seed + 7919 * k));
        });
        for (int k = 0; k < list.Count; ++k)
            pool._units.Add(new Unit { Grid = grids[k], Score = pool.ScoreUnit(grids[k]) });
        return pool;
    }

    /// <summary>Per-block raw fitness of a unit evaluated on its own box; negative infinity if it violates the heat constraint.</summary>
    public double ScoreUnit(Grid3 unit)
    {
        var s = ClassicSeeding.UnitSettings(_target, (unit.SizeX, unit.SizeY, unit.SizeZ));
        var e = new ClassicEvaluation();
        new ClassicEvaluator(s).Run(unit, e);
        if (s.EnsureHeatNeutral && e.NetHeat > 0.0) return double.NegativeInfinity;
        double raw = ClassicOpt.GoalFitness(s, e);
        // Power and breeding are extensive (scale with volume); efficiency is already per cell.
        return s.Goal == ClassicGoal.Efficiency ? raw : raw / unit.Length;
    }

    public const int Capacity = 8;
    private int _adopted;
    public int AdoptedCount => _adopted;

    /// <summary>
    /// Crops a block the size of pool member <paramref name="index"/> from the interior of a converged design
    /// (away from the casing) and adds it to the pool as a new unit. A crop cannot be judged in isolation —
    /// at its cut edges coolers lose their cells — so no score gate is applied: it gets tried like any other
    /// unit and lives or dies by its episode outcomes. When the pool is full it replaces the member with the
    /// worst mean outcome among those tried at least three times (never the one being cropped from).
    /// </summary>
    public bool TryAdopt(int index, Grid3 converged, Rng rng)
    {
        var u = _units[index];
        var (ux, uy, uz) = u.Size;
        int rx = converged.SizeX - ux - 2, ry = converged.SizeY - uy - 2, rz = converged.SizeZ - uz - 2;
        if (rx < 0 || ry < 0 || rz < 0) return false; // no interior room for a crop that avoids the walls
        int ox = 1 + rng.NextInt(rx), oy = 1 + rng.NextInt(ry), oz = 1 + rng.NextInt(rz);
        var crop = new Grid3(ux, uy, uz);
        for (int x = 0; x < ux; ++x)
        for (int y = 0; y < uy; ++y)
        for (int z = 0; z < uz; ++z)
        {
            int t = converged[ox + x, oy + y, oz + z];
            // Casing-dependent coolers are dead weight in a bulk unit; leave air so the search fills it.
            crop[x, y, z] = t == Lapis || t == Helium || t == Enderium || t == Magnesium ? Air : t;
        }
        if (crop.Data.AsSpan().SequenceEqual(u.Grid.Data)) return false; // nothing new
        var unit = new Unit { Grid = crop, Score = ScoreUnit(crop), Adopted = 1 };
        if (_units.Count < Capacity)
        {
            _units.Add(unit);
        }
        else
        {
            int worst = -1;
            for (int i = 0; i < _units.Count; ++i)
                if (i != index && _units[i].Uses >= 3 && (worst < 0 || _units[i].MeanOutcome < _units[worst].MeanOutcome))
                    worst = i;
            if (worst < 0) return false;
            _units[worst] = unit;
        }
        ++_adopted;
        return true;
    }

    /// <summary>
    /// Picks the unit for the next episode: untried units first in descending torus score (with the net on,
    /// episodes chain and the first pick may be the only one ever used), then epsilon-greedy on mean episode outcome.
    /// </summary>
    public int Pick(Rng rng)
    {
        if (_units.Count == 1) return 0;
        int untried = -1;
        for (int i = 0; i < _units.Count; ++i)
            if (_units[i].Uses == 0 && !_pending.Contains(i) && (untried < 0 || _units[i].Score > _units[untried].Score))
                untried = i;
        if (untried >= 0) { _pending.Add(untried); return untried; }
        if (rng.NextDouble() < Epsilon) return rng.NextInt(_units.Count - 1);
        int best = 0;
        for (int i = 1; i < _units.Count; ++i)
            if (_units[i].MeanOutcome > _units[best].MeanOutcome) best = i;
        return best;
    }

    // Units picked but whose first rollout has not finished yet (so "untried" is not re-picked).
    private readonly HashSet<int> _pending = new();

    /// <summary>Records how the episode seeded by unit <paramref name="index"/> ended (raw fitness of its converged design, 0 if infeasible).</summary>
    public void Report(int index, double outcome)
    {
        var u = _units[index];
        ++u.Uses;
        u.OutcomeSum += outcome;
        if (outcome > u.BestOutcome) u.BestOutcome = outcome;
        _pending.Remove(index);
    }
}
