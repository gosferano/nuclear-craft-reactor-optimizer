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
public sealed class ClassicOpt : IOptimizer<ClassicSample>
{
    public const int StageTrain = -2;
    public const int StageInfer = -1;
    /// <summary>Minimum number of steps between redraws of the best design (the web UI's redraw throttle).</summary>
    public const int InteractiveMin = 1024;
    public const int NLossHistory = 256;

    private readonly ClassicSettings _settings;
    private readonly ClassicEvaluator _evaluator;
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
    public int Seed => _rng.Seed;
    /// <summary>Rolling window of the last <see cref="NLossHistory"/> training losses (oldest first; zeros until filled).</summary>
    public ReadOnlySpan<double> LossHistory => _lossHistory;

    public ClassicOpt(ClassicSettings settings, bool useNet, int seed)
    {
        _settings = settings;
        _evaluator = new ClassicEvaluator(settings);
        _rng = new Rng(seed);
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

        Restart();
        if (useNet)
        {
            _net = new ClassicNet(settings, _rng);
            _net.AppendTrajectory(_parent);
        }
        _parentFitness = CurrentFitness(_parent);

        _best.State.Fill(Air);
        _evaluator.Run(_best.State, _best.Value);
    }

    /// <summary>Mirrors <c>Opt::restart</c>: fills the parent with random tiles, respecting budgets and symmetry.</summary>
    private void Restart()
    {
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
        _evaluator.Run(_parent.State, _parent.Value);
    }

    public bool Feasible(ClassicEvaluation x) => !_settings.EnsureHeatNeutral || x.NetHeat <= 0.0;

    public double RawFitness(ClassicEvaluation x)
    {
        switch (_settings.Goal)
        {
            default:
                return x.AvgMult;
            case ClassicGoal.Breeder:
                return x.AvgBreed;
            case ClassicGoal.Efficiency:
                return _settings.EnsureHeatNeutral ? (x.Efficiency - 1) * x.DutyCycle : x.Efficiency - 1;
        }
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

    private void MutateAndEvaluate(ClassicSample sample, int x, int y, int z)
    {
        int nSym = GetNSym(x, y, z);
        int oldTile = sample.State[x, y, z];
        if (oldTile != Air)
            sample.Limit[oldTile] += nSym;
        _allowedTiles.Clear();
        _allowedTiles.Add(Air);
        for (int tile = 0; tile < Air; ++tile)
            if (sample.Limit[tile] < 0 || sample.Limit[tile] >= nSym)
                _allowedTiles.Add(tile);
        int newTile = _allowedTiles[_rng.NextInt(_allowedTiles.Count - 1)];
        if (newTile != Air)
            sample.Limit[newTile] -= nSym;
        SetTileWithSym(sample, x, y, z, newTile);
        _evaluator.Run(sample.State, sample.Value);
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
                    Restart();
                _net!.NewTrajectory();
                _net.AppendTrajectory(_parent);
            }
            else if (Feasible(_parent.Value) || _infeasibilityPenalty > 1e8)
            {
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
            _best.CopyFrom(_parent);
        int bestChild = 0;
        double bestFitness = 0.0;
        for (int i = 0; i < _children.Length; ++i)
        {
            var child = _children[i];
            child.State.CopyFrom(_parent.State);
            Array.Copy(_parent.Limit, child.Limit, NumPlaceable);
            MutateAndEvaluate(child, _rng.NextInt(_settings.SizeX - 1), _rng.NextInt(_settings.SizeY - 1), _rng.NextInt(_settings.SizeZ - 1));
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
