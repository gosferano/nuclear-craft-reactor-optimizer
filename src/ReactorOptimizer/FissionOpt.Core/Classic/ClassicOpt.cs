using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Port of <c>Fission::Opt</c> (OptFission.cpp): stochastic hill-climbing with four children per
/// step, an escalating infeasibility penalty for heat-positive parents, and an optional value
/// network that alternates rollout / train / infer stages.
///
/// Not thread-safe: call <see cref="Step"/> from one thread. <see cref="Best"/> is only safe to
/// read from that same thread; a UI should copy it (see <see cref="ClassicSample.CopyFrom"/>)
/// when <see cref="NeedsRedrawBest"/> reports a change.
/// </summary>
public sealed class ClassicOpt : IOptimizer<ClassicSample>, IDisposable
{
    public const int StageTrain = -2;
    public const int StageInfer = -1;
    /// <summary>Minimum number of steps between redraws of the best design (the web UI's redraw throttle).</summary>
    public const int InteractiveMin = 1024;
    public const int NLossHistory = 256;

    /// <summary>Grids at least this large evaluate their four children on separate threads (see <see cref="ParallelChildren"/>).</summary>
    public const int ParallelChildrenThreshold = 300;

    private readonly ClassicSettings _settings;
    private readonly ClassicEvaluator _evaluator;
    // Parallel children: helper thread k evaluates _children[k] with _helperEvaluators[k]; the main thread does child 0.
    private readonly bool _parallelChildren;
    private readonly Thread[] _helpers = Array.Empty<Thread>();
    private readonly ClassicEvaluator[] _helperEvaluators = Array.Empty<ClassicEvaluator>();
    private readonly ManualResetEventSlim[] _helperStart = Array.Empty<ManualResetEventSlim>();
    private readonly CountdownEvent? _helperDone;
    private readonly Exception?[] _helperErrors = Array.Empty<Exception?>();
    private volatile bool _shutdown;
    // Incremental evaluation: the parent grid is evaluated in place by _inc; children are evaluated as
    // parent + mutation with undo, and only the winning mutation is applied.
    private readonly bool _incremental;
    private readonly IncrementalClassicEvaluator? _inc;
    private readonly int[][] _childLimit = Array.Empty<int[]>();
    private readonly ClassicEvaluation[] _childValue = Array.Empty<ClassicEvaluation>();
    private readonly (int x, int y, int z, int tile)[] _childMutation = Array.Empty<(int, int, int, int)>();
    // Tiling-seeded restarts (see ClassicSeeding / ClassicUnitPool): null = upstream's random restarts.
    private readonly ClassicUnitPool? _unitPool;
    private readonly double _tileNoise;
    private readonly bool _adoptUnits;
    private int _currentUnit = -1;
    private readonly List<Coord> _allowedCoords = new();
    private readonly List<int> _allowedTiles = new();
    private int _nEpisode, _nStage, _nIteration;
    private int _nConverge;
    private readonly int _maxConverge;
    private double _infeasibilityPenalty;
    private double _parentFitness;
    private ClassicSample _parent;
    private readonly ClassicSample _best;
    private readonly ClassicSample[] _children = new ClassicSample[4];
    private readonly Rng _rng;
    private readonly ClassicNet? _net;
    private bool _inferenceFailed;
    private bool _bestChanged;
    private int _redrawNagle;
    private readonly double[] _lossHistory = new double[NLossHistory];
    private bool _lossChanged;

    public ClassicSettings Settings => _settings;
    public ClassicSample Best => _best;
    public int NEpisode => _nEpisode;
    public int NStage => _nStage;
    public int NIteration => _nIteration;
    public bool UsesNet => _net != null;
    /// <summary>True when the value net uses the SIMD kernels (false when there is no net).</summary>
    public bool SimdNet => _net?.Simd ?? false;
    /// <summary>True when the four children of each step are evaluated on separate threads.</summary>
    public bool ParallelChildren => _parallelChildren;
    /// <summary>True when mutations are evaluated incrementally instead of by full re-evaluation.</summary>
    public bool IncrementalEvaluation => _incremental;
    public int Seed => _rng.Seed;
    /// <summary>Rolling window of the last <see cref="NLossHistory"/> training losses (oldest first; zeros until filled).</summary>
    public ReadOnlySpan<double> LossHistory => _lossHistory;

    /// <param name="parallelChildren">Evaluate the four children of each step concurrently. Null = automatic
    /// (on for grids of at least <see cref="ParallelChildrenThreshold"/> tiles). Results are identical either way.
    /// Ignored when incremental evaluation is active.</param>
    /// <param name="incrementalEvaluation">Evaluate mutations incrementally (see <see cref="IncrementalClassicEvaluator"/>).
    /// Null = automatic (on whenever <see cref="IncrementalClassicEvaluator.Supports"/> the settings). Mathematically the
    /// same evaluation, but totals can differ from the scalar evaluator in the last bit, so a seed is only guaranteed to
    /// reproduce a run made with the same setting.</param>
    /// <param name="simdNet">Use the Vector256 kernels in the value net (faster; results reproducible on every CPU but
    /// differ in the last bits from the scalar loops, so a seed reproduces a run only with the same setting).</param>
    /// <param name="unitPool">If set, every episode restarts from a unit design from this pool tiled over the grid
    /// (random phase shift, <paramref name="tileNoise"/> fraction of tiles randomized) instead of a random grid;
    /// units are chosen by episode outcome. <paramref name="adoptUnits"/> additionally adds crops of converged designs
    /// to the pool; measured slightly negative at 5-minute budgets, so off by default. Not upstream behaviour.</param>
    public ClassicOpt(ClassicSettings settings, bool useNet, int seed, bool? parallelChildren = null, bool? incrementalEvaluation = null, bool simdNet = true,
        ClassicUnitPool? unitPool = null, double tileNoise = 0.02, bool adoptUnits = false)
    {
        _settings = settings;
        _unitPool = unitPool;
        _tileNoise = tileNoise;
        _adoptUnits = adoptUnits;
        _evaluator = new ClassicEvaluator(settings);
        _rng = new Rng(seed);
        _incremental = incrementalEvaluation ?? IncrementalClassicEvaluator.Supports(settings);
        if (_incremental && !IncrementalClassicEvaluator.Supports(settings))
            throw new ArgumentException("incremental evaluation cannot model active-cooler accessibility; disable active coolers or the accessibility check", nameof(incrementalEvaluation));
        _parallelChildren = !_incremental && (parallelChildren ?? settings.Volume >= ParallelChildrenThreshold);
        if (_parallelChildren)
        {
            int n = _children.Length - 1;
            _helpers = new Thread[n];
            _helperEvaluators = new ClassicEvaluator[n];
            _helperStart = new ManualResetEventSlim[n];
            _helperErrors = new Exception?[n];
            _helperDone = new CountdownEvent(n);
            for (int k = 0; k < n; ++k)
            {
                _helperEvaluators[k] = new ClassicEvaluator(settings);
                _helperStart[k] = new ManualResetEventSlim(false, spinCount: 2000);
                int index = k;
                _helpers[k] = new Thread(() => HelperLoop(index), 16 * 1024 * 1024) { IsBackground = true, Name = "FissionOpt child " + (k + 1) };
                _helpers[k].Start();
            }
        }
        _maxConverge = Math.Min(7 * 7 * 7, settings.Volume) * 16;
        _bestChanged = true;

        for (int x = settings.SymX ? settings.SizeX / 2 : 0; x < settings.SizeX; ++x)
        for (int y = settings.SymY ? settings.SizeY / 2 : 0; y < settings.SizeY; ++y)
        for (int z = settings.SymZ ? settings.SizeZ / 2 : 0; z < settings.SizeZ; ++z)
            _allowedCoords.Add(new Coord(x, y, z));

        _parent = new ClassicSample(settings.SizeX, settings.SizeY, settings.SizeZ);
        _best = new ClassicSample(settings.SizeX, settings.SizeY, settings.SizeZ);
        for (int i = 0; i < _children.Length; ++i)
            _children[i] = new ClassicSample(settings.SizeX, settings.SizeY, settings.SizeZ);
        if (_incremental)
        {
            _inc = new IncrementalClassicEvaluator(settings, _parent.State);
            _childLimit = new int[_children.Length][];
            _childValue = new ClassicEvaluation[_children.Length];
            _childMutation = new (int, int, int, int)[_children.Length];
            for (int i = 0; i < _children.Length; ++i)
            {
                _childLimit[i] = new int[NumPlaceable];
                _childValue[i] = new ClassicEvaluation();
            }
        }

        Restart();
        if (useNet)
        {
            _net = new ClassicNet(settings, _rng, simdNet);
            AppendParentTrajectory();
        }
        _parentFitness = CurrentFitness(_parent);

        _best.State.Fill(Air);
        _evaluator.Run(_best.State, _best.Value);
    }

    private void AppendParentTrajectory()
    {
        if (_inc != null)
            _net!.AppendTrajectory(_inc.CountByTile, _inc.InvalidByTile, _parent.Value);
        else
            _net!.AppendTrajectory(_parent);
    }

    /// <summary>True when episodes restart from a tiled seed design rather than a random grid.</summary>
    public bool TiledRestarts => _unitPool != null;
    public ClassicUnitPool? UnitPool => _unitPool;

    /// <summary>Mirrors <c>Opt::restart</c>: fills the parent with random tiles, respecting budgets and symmetry.</summary>
    private void Restart(bool keepUnit = false)
    {
        if (_unitPool != null)
        {
            if (!keepUnit || _currentUnit < 0)
                _currentUnit = _unitPool.Pick(_rng);
            TiledRestart(_unitPool.Units[_currentUnit].Grid);
            return;
        }
        _rng.Shuffle(_allowedCoords);
        Array.Copy(_settings.Limit, _parent.Limit, NumPlaceable);
        _parent.State.Fill(Air);
        foreach (var c in _allowedCoords)
        {
            int nSym = GetNSym(c.X, c.Y, c.Z);
            _allowedTiles.Clear();
            for (int tile = 0; tile < Air; ++tile)
                if (_parent.Limit[tile] < 0 || _parent.Limit[tile] >= nSym)
                    _allowedTiles.Add(tile);
            if (_allowedTiles.Count == 0)
                break;
            int newTile = _allowedTiles[_rng.NextInt(_allowedTiles.Count - 1)];
            _parent.Limit[newTile] -= nSym;
            SetTileWithSym(_parent, c.X, c.Y, c.Z, newTile);
        }
        EvaluateParent();
    }

    /// <summary>Restart from a unit design: random phase shift, a little noise, budgets and symmetry respected.</summary>
    private void TiledRestart(Grid3 unit)
    {
        _rng.Shuffle(_allowedCoords);
        Array.Copy(_settings.Limit, _parent.Limit, NumPlaceable);
        _parent.State.Fill(Air);
        int ox = _rng.NextInt(unit.SizeX - 1), oy = _rng.NextInt(unit.SizeY - 1), oz = _rng.NextInt(unit.SizeZ - 1);
        foreach (var c in _allowedCoords)
        {
            int nSym = GetNSym(c.X, c.Y, c.Z);
            int tile = unit[(c.X + ox) % unit.SizeX, (c.Y + oy) % unit.SizeY, (c.Z + oz) % unit.SizeZ];
            if (_tileNoise > 0 && _rng.NextDouble() < _tileNoise)
            {
                _allowedTiles.Clear();
                for (int t = 0; t < Air; ++t)
                    if (_parent.Limit[t] < 0 || _parent.Limit[t] >= nSym)
                        _allowedTiles.Add(t);
                tile = _allowedTiles.Count == 0 ? Air : _allowedTiles[_rng.NextInt(_allowedTiles.Count - 1)];
            }
            if (tile != Air && !(_parent.Limit[tile] < 0 || _parent.Limit[tile] >= nSym))
                tile = Air; // budget exhausted for this block type
            if (tile != Air)
                _parent.Limit[tile] -= nSym;
            SetTileWithSym(_parent, c.X, c.Y, c.Z, tile);
        }
        EvaluateParent();
    }

    private void EvaluateParent()
    {
        if (_inc != null)
        {
            _inc.Rebuild();
            _inc.WriteTo(_parent.Value, withInvalidList: false);
        }
        else
        {
            _evaluator.Run(_parent.State, _parent.Value);
        }
    }

    public bool Feasible(ClassicEvaluation x) => !_settings.EnsureHeatNeutral || x.NetHeat <= 0.0;

    public double RawFitness(ClassicEvaluation x) => GoalFitness(_settings, x);

    /// <summary>Upstream's rawFitness, plus the optional cooling-surplus tie-break (see <see cref="ClassicSettings.SurplusTieBreak"/>).</summary>
    public static double GoalFitness(ClassicSettings s, ClassicEvaluation x)
    {
        double f = s.Goal switch
        {
            ClassicGoal.Breeder => x.AvgBreed,
            ClassicGoal.Efficiency => s.EnsureHeatNeutral ? (x.Efficiency - 1) * x.DutyCycle : x.Efficiency - 1,
            _ => x.AvgMult,
        };
        if (s.SurplusTieBreak > 0)
        {
            double maxRate = 0;
            foreach (var r in s.CoolingRates) if (r > maxRate) maxRate = r;
            if (maxRate > 0)
                f += s.SurplusTieBreak * (x.Cooling - x.Heat) / (maxRate * s.Volume);
        }
        return f;
    }

    /// <summary>Fitness of the incremental evaluator's current (parent + mutation) state.</summary>
    private double CurrentFitnessIncremental(ClassicEvaluation value)
    {
        if (_nStage == StageInfer)
            return _net!.Infer(_inc!.CountByTile, _inc.InvalidByTile, value);
        if (_nStage == StageTrain)
            return 0.0;
        if (Feasible(value))
            return RawFitness(value);
        return RawFitness(value) - value.NetHeat / _settings.FuelBaseHeat * _infeasibilityPenalty;
    }

    private double CurrentFitness(ClassicSample x)
    {
        if (_nStage == StageInfer)
            return _net!.Infer(x);
        if (_nStage == StageTrain)
            return 0.0;
        if (Feasible(x.Value))
            return RawFitness(x.Value);
        return RawFitness(x.Value) - x.Value.NetHeat / _settings.FuelBaseHeat * _infeasibilityPenalty;
    }

    /// <summary>How many tiles one mutation at (x,y,z) writes under the active mirror symmetries: 1, 2, 4 or 8.</summary>
    private int GetNSym(int x, int y, int z)
    {
        int result = 1;
        if (_settings.SymX && x != _settings.SizeX - x - 1) result *= 2;
        if (_settings.SymY && y != _settings.SizeY - y - 1) result *= 2;
        if (_settings.SymZ && z != _settings.SizeZ - z - 1) result *= 2;
        return result;
    }

    private void SetTileWithSym(ClassicSample sample, int x, int y, int z, int tile)
    {
        var s = sample.State;
        int mx = _settings.SizeX - x - 1, my = _settings.SizeY - y - 1, mz = _settings.SizeZ - z - 1;
        s[x, y, z] = tile;
        if (_settings.SymX)
        {
            s[mx, y, z] = tile;
            if (_settings.SymY)
            {
                s[x, my, z] = tile;
                s[mx, my, z] = tile;
                if (_settings.SymZ)
                {
                    s[x, y, mz] = tile;
                    s[mx, y, mz] = tile;
                    s[x, my, mz] = tile;
                    s[mx, my, mz] = tile;
                }
            }
            else if (_settings.SymZ)
            {
                s[x, y, mz] = tile;
                s[mx, y, mz] = tile;
            }
        }
        else if (_settings.SymY)
        {
            s[x, my, z] = tile;
            if (_settings.SymZ)
            {
                s[x, y, mz] = tile;
                s[x, my, mz] = tile;
            }
        }
        else if (_settings.SymZ)
        {
            s[x, y, mz] = tile;
        }
    }

    /// <summary>The RNG-consuming half of <c>mutateAndEvaluate</c>: pick and apply a tile change, keeping the budget counters consistent.</summary>
    private void Mutate(ClassicSample sample, int x, int y, int z)
    {
        int newTile = DrawNewTile(sample.Limit, sample.State[x, y, z], GetNSym(x, y, z));
        SetTileWithSym(sample, x, y, z, newTile);
    }

    /// <summary>Draws the replacement tile for a mutation and updates <paramref name="limit"/> (the budget) accordingly.</summary>
    private int DrawNewTile(int[] limit, int oldTile, int nSym)
    {
        if (oldTile != Air)
            limit[oldTile] += nSym;
        _allowedTiles.Clear();
        _allowedTiles.Add(Air);
        for (int tile = 0; tile < Air; ++tile)
            if (limit[tile] < 0 || limit[tile] >= nSym)
                _allowedTiles.Add(tile);
        int newTile = _allowedTiles[_rng.NextInt(_allowedTiles.Count - 1)];
        if (newTile != Air)
            limit[newTile] -= nSym;
        return newTile;
    }

    /// <summary>Queues a mutation (with its mirror images) on the incremental evaluator, same positions as <see cref="SetTileWithSym"/>.</summary>
    private void QueueWithSym(int x, int y, int z, int tile)
    {
        var inc = _inc!;
        int mx = _settings.SizeX - x - 1, my = _settings.SizeY - y - 1, mz = _settings.SizeZ - z - 1;
        inc.Set(x, y, z, tile);
        if (_settings.SymX)
        {
            inc.Set(mx, y, z, tile);
            if (_settings.SymY)
            {
                inc.Set(x, my, z, tile);
                inc.Set(mx, my, z, tile);
                if (_settings.SymZ)
                {
                    inc.Set(x, y, mz, tile);
                    inc.Set(mx, y, mz, tile);
                    inc.Set(x, my, mz, tile);
                    inc.Set(mx, my, mz, tile);
                }
            }
            else if (_settings.SymZ)
            {
                inc.Set(x, y, mz, tile);
                inc.Set(mx, y, mz, tile);
            }
        }
        else if (_settings.SymY)
        {
            inc.Set(x, my, z, tile);
            if (_settings.SymZ)
            {
                inc.Set(x, y, mz, tile);
                inc.Set(x, my, mz, tile);
            }
        }
        else if (_settings.SymZ)
        {
            inc.Set(x, y, mz, tile);
        }
    }

    /// <summary>
    /// The children part of <see cref="Step"/> in incremental mode: each child is the parent plus one
    /// mutation, evaluated in place and undone; the winner (if it is at least as fit as the parent) is
    /// re-applied and committed. Same RNG consumption and decisions as the materialized path.
    /// </summary>
    private void StepChildrenIncremental(ref bool bestChangedLocal)
    {
        var inc = _inc!;
        int bestChild = 0;
        double bestFitness = 0.0;
        for (int i = 0; i < _children.Length; ++i)
        {
            int x = _rng.NextInt(_settings.SizeX - 1), y = _rng.NextInt(_settings.SizeY - 1), z = _rng.NextInt(_settings.SizeZ - 1);
            Array.Copy(_parent.Limit, _childLimit[i], NumPlaceable);
            int newTile = DrawNewTile(_childLimit[i], _parent.State[x, y, z], GetNSym(x, y, z));
            _childMutation[i] = (x, y, z, newTile);
            QueueWithSym(x, y, z, newTile);
            inc.Apply();
            var value = _childValue[i];
            inc.WriteTo(value, withInvalidList: false);
            double fitness = CurrentFitnessIncremental(value);
            if (i == 0 || fitness > bestFitness)
            {
                bestChild = i;
                bestFitness = fitness;
            }
            if (Feasible(value) && RawFitness(value) > RawFitness(_best.Value))
            {
                bestChangedLocal = true;
                // The grid currently holds this child; materialize it as the new best.
                _best.State.CopyFrom(_parent.State);
                Array.Copy(_childLimit[i], _best.Limit, NumPlaceable);
                inc.WriteTo(_best.Value, withInvalidList: true);
            }
            inc.Undo();
        }
        if (bestFitness >= _parentFitness)
        {
            if (bestFitness > _parentFitness)
            {
                _parentFitness = bestFitness;
                _nConverge = 0;
                if (_nStage == StageInfer)
                    _inferenceFailed = false;
            }
            var (bx, by, bz, bt) = _childMutation[bestChild];
            QueueWithSym(bx, by, bz, bt);
            inc.Apply();
            inc.Commit();
            Array.Copy(_childLimit[bestChild], _parent.Limit, NumPlaceable);
            inc.WriteTo(_parent.Value, withInvalidList: false);
            if (_net != null && _nStage != StageInfer)
                _net.AppendTrajectory(inc.CountByTile, inc.InvalidByTile, _parent.Value);
        }
    }

    /// <summary>
    /// Evaluates all children. Mutations were already drawn sequentially, and the evaluator is a pure
    /// function of the grid, so running the four evaluations concurrently yields exactly the results
    /// the sequential loop would.
    /// </summary>
    private void EvaluateChildren()
    {
        if (!_parallelChildren)
        {
            foreach (var child in _children)
                _evaluator.Run(child.State, child.Value);
            return;
        }
        foreach (var start in _helperStart)
            start.Set();
        _evaluator.Run(_children[0].State, _children[0].Value);
        _helperDone!.Wait();
        _helperDone.Reset();
        for (int k = 0; k < _helperErrors.Length; ++k)
            if (_helperErrors[k] != null)
                throw new InvalidOperationException("child evaluation failed on a helper thread", _helperErrors[k]);
    }

    private void HelperLoop(int k)
    {
        var start = _helperStart[k];
        var evaluator = _helperEvaluators[k];
        while (true)
        {
            start.Wait();
            if (_shutdown) return;
            start.Reset();
            try
            {
                var child = _children[k + 1];
                evaluator.Run(child.State, child.Value);
            }
            catch (Exception e)
            {
                _helperErrors[k] = e;
            }
            _helperDone!.Signal();
        }
    }

    /// <summary>Stops the helper threads (if any). Safe to call more than once.</summary>
    public void Dispose()
    {
        _shutdown = true;
        foreach (var start in _helperStart)
            start.Set();
    }

    /// <summary>Mirrors <c>Opt::step</c>: one training iteration, or one hill-climbing iteration (four mutated children).</summary>
    public void Step()
    {
        ++_redrawNagle;
        if (_nStage == StageTrain)
        {
            if (_nIteration == 0)
            {
                _nStage = StageInfer;
                // As upstream: the net-guided climb starts from the converged design, and a restart follows
                // only if that climb finds nothing. Episodes therefore chain, which is what lets long runs
                // keep improving; tiled restarts apply wherever a restart happens (first episode, failed
                // inference, or every episode when the net is off). Restarting every episode from a fresh
                // tiling was measured to win the first minutes and then plateau, so it is not done.
                _parentFitness = _net!.Infer(_parent);
                _inferenceFailed = true;
            }
            else
            {
                Array.Copy(_lossHistory, 1, _lossHistory, 0, NLossHistory - 1);
                _lossHistory[NLossHistory - 1] = _net!.Train();
                _lossChanged = true;
                --_nIteration;
                return;
            }
        }

        if (_nConverge == _maxConverge)
        {
            _nIteration = 0;
            _nConverge = 0;
            if (_nStage == StageInfer)
            {
                _nStage = 0;
                ++_nEpisode;
                if (_inferenceFailed)
                    Restart(keepUnit: true); // the net found nothing from this tiling; try the same unit with a new shift
                _net!.NewTrajectory();
                AppendParentTrajectory();
            }
            else if (Feasible(_parent.Value) || _infeasibilityPenalty > 1e8)
            {
                // A rollout has converged. With the net on, the next episode usually continues from this
                // design (a restart happens only if inference fails), so this is where the unit that
                // seeded the lineage gets its outcome, and where a refined crop can flow back into the pool.
                if (_unitPool != null && _currentUnit >= 0)
                {
                    bool feasible = Feasible(_parent.Value);
                    _unitPool.Report(_currentUnit, feasible ? RawFitness(_parent.Value) : 0.0);
                    if (_adoptUnits && feasible)
                        _unitPool.TryAdopt(_currentUnit, _parent.State, _rng);
                }
                _infeasibilityPenalty = 0.0;
                if (_net != null)
                {
                    _nStage = StageTrain;
                    _net.FinishTrajectory(Feasible(_parent.Value) ? RawFitness(_parent.Value) : 0.0);
                    _nIteration = (_net.TrajectoryLength * ClassicNet.NEpoch + ClassicNet.NMiniBatch - 1) / ClassicNet.NMiniBatch;
                    return;
                }
                else
                {
                    _nStage = 0;
                    ++_nEpisode;
                    Restart();
                }
            }
            else
            {
                ++_nStage;
                if (_infeasibilityPenalty != 0.0)
                    _infeasibilityPenalty *= 2;
                else
                    _infeasibilityPenalty = _rng.NextDouble();
            }
            _parentFitness = CurrentFitness(_parent);
        }

        bool bestChangedLocal = _nEpisode == 0 && _nStage == 0 && _nIteration == 0 && Feasible(_parent.Value);
        if (bestChangedLocal)
        {
            if (_inc != null) _inc.WriteTo(_parent.Value, withInvalidList: true);
            _best.CopyFrom(_parent);
        }
        if (_inc != null)
        {
            StepChildrenIncremental(ref bestChangedLocal);
            FinishStep(bestChangedLocal);
            return;
        }
        int bestChild = 0;
        double bestFitness = 0.0;
        for (int i = 0; i < _children.Length; ++i)
        {
            var child = _children[i];
            child.State.CopyFrom(_parent.State);
            Array.Copy(_parent.Limit, child.Limit, NumPlaceable);
            Mutate(child, _rng.NextInt(_settings.SizeX - 1), _rng.NextInt(_settings.SizeY - 1), _rng.NextInt(_settings.SizeZ - 1));
        }
        EvaluateChildren();
        for (int i = 0; i < _children.Length; ++i)
        {
            var child = _children[i];
            double fitness = CurrentFitness(child);
            if (i == 0 || fitness > bestFitness)
            {
                bestChild = i;
                bestFitness = fitness;
            }
            if (Feasible(child.Value) && RawFitness(child.Value) > RawFitness(_best.Value))
            {
                bestChangedLocal = true;
                _best.CopyFrom(child);
            }
        }
        if (bestFitness >= _parentFitness)
        {
            if (bestFitness > _parentFitness)
            {
                _parentFitness = bestFitness;
                _nConverge = 0;
                if (_nStage == StageInfer)
                    _inferenceFailed = false;
            }
            // std::swap(parent, child): the child slot now holds the old parent, to be overwritten next step.
            (_parent, _children[bestChild]) = (_children[bestChild], _parent);
            if (_net != null && _nStage != StageInfer)
                _net.AppendTrajectory(_parent);
        }
        FinishStep(bestChangedLocal);
    }

    private void FinishStep(bool bestChangedLocal)
    {
        ++_nConverge;
        ++_nIteration;
        if (bestChangedLocal)
        {
            foreach (var c in _best.Value.InvalidTiles)
                _best.State[c] = Air;
            _bestChanged = true;
        }
    }

    public ClassicSample CreateSample() => new(_settings.SizeX, _settings.SizeY, _settings.SizeZ);
    public void CopySample(ClassicSample from, ClassicSample to) => to.CopyFrom(from);

    /// <summary>True (once) when the best design changed and at least <see cref="InteractiveMin"/> steps have run since the last redraw.</summary>
    public bool NeedsRedrawBest()
    {
        bool result = _bestChanged && _redrawNagle >= InteractiveMin;
        if (result)
        {
            _bestChanged = false;
            _redrawNagle = 0;
        }
        return result;
    }

    public bool NeedsReplotLoss()
    {
        bool result = _lossChanged;
        if (result)
            _lossChanged = false;
        return result;
    }
}
