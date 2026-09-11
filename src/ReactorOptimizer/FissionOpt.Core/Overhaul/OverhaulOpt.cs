using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Core.Overhaul;

/// <summary>
/// Port of <c>OverhaulFission::Opt</c> (OptOverhaulFission.cpp): single-child hill climbing with two
/// adaptively weighted constraint penalties (positive net heat; cells still active with shields on
/// when the reactor must be controllable), and a value net that alternates rollout / train / infer.
/// Debug <c>std::cout</c> lines from the original are dropped. Not thread-safe.
/// </summary>
public sealed class OverhaulOpt : IOptimizer<OverhaulSample>
{
    public const int StageRollout = 0;
    public const int StageTrain = 1;
    public const int StageInfer = 2;
    /// <summary>Minimum number of steps between redraws of the best design.</summary>
    public const int InteractiveMin = 4096;
    public const int NLossHistory = 256;
    public const int MaxConvergeInfer = 10976;
    public const int MaxConvergeRollout = MaxConvergeInfer * 100;
    public const int NConstraints = 2;
    public const int PenaltyUpdatePeriod = MaxConvergeInfer;

    private readonly Rng _rng;
    private readonly OverhaulSettings _settings;
    private readonly List<Coord> _allowedCoords = new();
    private readonly List<int> _allowedTiles = new();
    private int _nEpisode, _nStage, _nIteration;
    private int _nConverge;
    private readonly bool[] _hasFeasible = new bool[NConstraints], _hasInfeasible = new bool[NConstraints];
    private readonly double[] _penalty = { 1.0, 1.0 };
    private readonly List<double[]> _trajectoryBuffer = new();
    private int _trajectoryBufferCount;
    private double _parentFitness, _localBest;
    private OverhaulSample _parent, _child;
    private readonly OverhaulSample _best;
    private readonly OverhaulNet _net;
    private bool _inferenceFailed;
    private bool _bestChanged;
    private int _redrawNagle;
    private readonly double[] _lossHistory = new double[NLossHistory];
    private bool _lossChanged;
    private readonly bool[] _feasibleScratch = new bool[NConstraints];
    private readonly double[] _infeasibilityScratch = new double[NConstraints];

    public OverhaulSettings Settings => _settings;
    public OverhaulSample Best => _best;
    public int NEpisode => _nEpisode;
    public int NStage => _nStage;
    public int NIteration => _nIteration;
    public int Seed => _rng.Seed;
    public ReadOnlySpan<double> LossHistory => _lossHistory;

    /// <summary>Calls <c>settings.Compute()</c>, like the C++ constructor.</summary>
    public OverhaulOpt(OverhaulSettings settings, int seed)
    {
        _settings = settings;
        _rng = new Rng(seed);
        _nStage = StageRollout;
        _bestChanged = true;
        settings.Compute();
        if (settings.Fuels.Count == 0) throw new ArgumentException("at least one fuel is required", nameof(settings));
        for (int x = settings.SymX ? settings.SizeX / 2 : 0; x < settings.SizeX; ++x)
        for (int y = settings.SymY ? settings.SizeY / 2 : 0; y < settings.SizeY; ++y)
        for (int z = settings.SymZ ? settings.SizeZ / 2 : 0; z < settings.SizeZ; ++z)
            _allowedCoords.Add(new Coord(x, y, z));

        _parent = new OverhaulSample(settings);
        Restart();
        _net = new OverhaulNet(settings, _rng);
        _net.AppendTrajectory(_parent);
        _parentFitness = CurrentFitness(_parent);
        _localBest = AllFeasible(_parent) ? _parentFitness : 0.0;

        _child = new OverhaulSample(settings);

        _best = new OverhaulSample(settings);
        _best.State.Fill(Air);
        _best.Value.Run(_best.State);
    }

    /// <summary>Mirrors <c>Opt::restart</c>.</summary>
    private void Restart()
    {
        _rng.Shuffle(_allowedCoords);
        Array.Copy(_settings.Limits, _parent.Limits, NumLimited);
        Array.Copy(_settings.SourceLimits, _parent.SourceLimits, 3);
        for (int i = 0; i < _settings.Fuels.Count; ++i)
            _parent.CellLimits[i] = _settings.Fuels[i].Limit;
        _parent.State.Fill(Air);
        foreach (var c in _allowedCoords)
        {
            int nSym = GetNSym(c.X, c.Y, c.Z);
            _allowedTiles.Clear();
            for (int tile = 0; tile < Air; ++tile)
                if (_parent.Limits[tile] < 0 || _parent.Limits[tile] >= nSym)
                    _allowedTiles.Add(tile);
            AddAllowedCells(_parent, nSym);
            if (_allowedTiles.Count == 0)
                break;
            int newTile = _allowedTiles[_rng.NextInt(_allowedTiles.Count - 1)];
            if (newTile < Air)
            {
                _parent.Limits[newTile] -= nSym;
            }
            else
            {
                var (fuel, source) = _settings.CellTypes[newTile - C0];
                _parent.CellLimits[fuel] -= nSym;
                if (source != 0)
                    _parent.SourceLimits[source - 1] -= nSym;
            }
            SetTileWithSym(_parent, c.X, c.Y, c.Z, newTile);
        }
        _parent.Evaluate();
    }

    private void AddAllowedCells(OverhaulSample sample, int nSym)
    {
        var cellTypes = _settings.CellTypes;
        for (int cell = 0; cell < cellTypes.Count; ++cell)
        {
            var (fuel, source) = cellTypes[cell];
            if (sample.CellLimits[fuel] >= 0 && sample.CellLimits[fuel] < nSym)
                continue;
            if (source != 0 && sample.SourceLimits[source - 1] >= 0 && sample.SourceLimits[source - 1] < nSym)
                continue;
            _allowedTiles.Add(C0 + cell);
        }
    }

    /// <summary>Mirrors <c>Opt::feasible</c>: [no positive net heat, shuts down with shields on (if controllable)].</summary>
    private bool[] Feasible(OverhaulSample x)
    {
        _feasibleScratch[0] = x.Value.TotalPositiveNetHeat == 0;
        _feasibleScratch[1] = !_settings.Controllable || x.ValueWithShield.NActiveCells == 0;
        return _feasibleScratch;
    }

    public bool AllFeasible(OverhaulSample x)
    {
        var f = Feasible(x);
        return f[0] && f[1];
    }

    private double[] Infeasibility(OverhaulSample x)
    {
        _infeasibilityScratch[0] = (double)x.Value.TotalPositiveNetHeat / _settings.MinHeat;
        _infeasibilityScratch[1] = _settings.Controllable ? x.ValueWithShield.NActiveCells : 0.0;
        return _infeasibilityScratch;
    }

    public double RawFitness(OverhaulEvaluation x)
    {
        switch (_settings.Goal)
        {
            default:
                return x.Output / _settings.MaxOutput;
            case OverhaulGoal.FuelUse:
                return x.NActiveCells;
            case OverhaulGoal.Efficiency:
                return x.Efficiency;
            case OverhaulGoal.Irradiation:
                return (double)x.IrradiatorFlux / _settings.MinCriticality;
        }
    }

    private double CurrentFitness(OverhaulSample x)
    {
        if (_nStage == StageInfer)
            return _net.Infer(x);
        if (_nStage == StageTrain)
            return 0.0;
        double result = RawFitness(x.Value);
        result += Math.Min(x.Value.TotalRawFlux, _settings.MinCriticality) / (double)_settings.MinCriticality;
        result += Math.Min(x.Value.MaxCellFlux, _settings.MinCriticality) / (double)_settings.MinCriticality;
        var inf = Infeasibility(x);
        result -= inf[0] * _penalty[0] + inf[1] * _penalty[1];
        return result;
    }

    private int GetNSym(int x, int y, int z)
    {
        int result = 1;
        if (_settings.SymX && x != _settings.SizeX - x - 1) result *= 2;
        if (_settings.SymY && y != _settings.SizeY - y - 1) result *= 2;
        if (_settings.SymZ && z != _settings.SizeZ - z - 1) result *= 2;
        return result;
    }

    private void SetTileWithSym(OverhaulSample sample, int x, int y, int z, int tile)
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

    private void MutateAndEvaluate(OverhaulSample sample, int x, int y, int z)
    {
        int nSym = GetNSym(x, y, z);
        int oldTile = sample.State[x, y, z];
        if (oldTile < Air)
        {
            sample.Limits[oldTile] += nSym;
        }
        else if (oldTile >= C0)
        {
            var (fuel, source) = _settings.CellTypes[oldTile - C0];
            sample.CellLimits[fuel] += nSym;
            if (source != 0)
                sample.SourceLimits[source - 1] += nSym;
        }
        _allowedTiles.Clear();
        _allowedTiles.Add(Air);
        for (int tile = 0; tile < Air; ++tile)
            if (sample.Limits[tile] < 0 || sample.Limits[tile] >= nSym)
                _allowedTiles.Add(tile);
        AddAllowedCells(sample, nSym);
        int newTile = _allowedTiles[_rng.NextInt(_allowedTiles.Count - 1)];
        if (newTile < Air)
        {
            sample.Limits[newTile] -= nSym;
        }
        else if (newTile >= C0)
        {
            var (fuel, source) = _settings.CellTypes[newTile - C0];
            sample.CellLimits[fuel] -= nSym;
            if (source != 0)
                sample.SourceLimits[source - 1] -= nSym;
        }
        SetTileWithSym(sample, x, y, z, newTile);
        sample.Evaluate();
    }

    private void BufferTrajectory(OverhaulSample sample)
    {
        if (_trajectoryBufferCount == _trajectoryBuffer.Count)
            _trajectoryBuffer.Add(new double[_net.NFeatures]);
        _net.ExtractFeatures(sample, _trajectoryBuffer[_trajectoryBufferCount++]);
    }

    /// <summary>Mirrors <c>Opt::step</c>.</summary>
    public void Step()
    {
        ++_redrawNagle;
        if (_nStage == StageTrain)
        {
            if (_nIteration == 0)
            {
                _nStage = StageInfer;
                Restart();
                _parentFitness = CurrentFitness(_parent);
                _inferenceFailed = true;
            }
            else
            {
                Array.Copy(_lossHistory, 1, _lossHistory, 0, NLossHistory - 1);
                _lossHistory[NLossHistory - 1] = _net.Train();
                _lossChanged = true;
                --_nIteration;
                return;
            }
        }
        else if (_nStage == StageInfer)
        {
            if (_nConverge == MaxConvergeInfer)
            {
                _nStage = StageRollout;
                ++_nEpisode;
                if (_inferenceFailed)
                    Restart();
                _net.NewTrajectory();
                _net.AppendTrajectory(_parent);
                _parentFitness = CurrentFitness(_parent);
                _localBest = AllFeasible(_parent) ? _parentFitness : 0.0;
                _nConverge = 0;
                _nIteration = 0;
            }
        }
        else if (_nConverge == MaxConvergeRollout)
        {
            _nStage = StageTrain;
            _trajectoryBufferCount = 0;
            _net.FinishTrajectory(_localBest);
            _nConverge = 0;
            _nIteration = (_net.TrajectoryLength * OverhaulNet.NEpoch + OverhaulNet.NMiniBatch - 1) / OverhaulNet.NMiniBatch;
            return;
        }

        bool bestChangedLocal = _nEpisode == 0 && _nStage == StageRollout && _nIteration == 0 && AllFeasible(_parent);
        if (bestChangedLocal)
            _best.CopyFrom(_parent);
        _child.State.CopyFrom(_parent.State);
        Array.Copy(_parent.Limits, _child.Limits, NumLimited);
        Array.Copy(_parent.SourceLimits, _child.SourceLimits, 3);
        Array.Copy(_parent.CellLimits, _child.CellLimits, _child.CellLimits.Length);
        MutateAndEvaluate(_child, _rng.NextInt(_settings.SizeX - 1), _rng.NextInt(_settings.SizeY - 1), _rng.NextInt(_settings.SizeZ - 1));
        double childFitness = CurrentFitness(_child);
        if (AllFeasible(_child) && RawFitness(_child.Value) > RawFitness(_best.Value))
        {
            bestChangedLocal = true;
            _best.CopyFrom(_child);
        }
        if (childFitness >= _parentFitness)
        {
            if (childFitness > _parentFitness)
            {
                _parentFitness = childFitness;
                if (_nStage == StageInfer)
                {
                    _nConverge = 0;
                    _inferenceFailed = false;
                }
            }
            // The upstream line begins with a stray '-' (a discarded unary minus); it is just this call.
            if (_nStage != StageInfer && _rng.NextInt(9) == 0)
                BufferTrajectory(_child);
            (_parent, _child) = (_child, _parent);
        }

        if (_nStage != StageInfer)
        {
            var feasible = Feasible(_parent);
            if (feasible[0] && feasible[1])
            {
                if (_parentFitness > _localBest)
                {
                    _localBest = _parentFitness;
                    _nConverge = 0;
                    while (_trajectoryBufferCount > 0)
                        _net.AppendTrajectory(_trajectoryBuffer[--_trajectoryBufferCount]);
                }
            }
            for (int i = 0; i < NConstraints; ++i)
                if (feasible[i])
                    _hasFeasible[i] = true;
                else
                    _hasInfeasible[i] = true;
            if (_nIteration % PenaltyUpdatePeriod == 0)
            {
                for (int i = 0; i < NConstraints; ++i)
                {
                    if (_hasFeasible[i] && !_hasInfeasible[i])
                        _penalty[i] *= 0.5;
                    else if (!_hasFeasible[i] && _hasInfeasible[i])
                        _penalty[i] = Math.Max(0.001, _penalty[i] * 1.5);
                    _hasFeasible[i] = false;
                    _hasInfeasible[i] = false;
                }
            }
            _parentFitness = CurrentFitness(_parent);
        }

        ++_nConverge;
        ++_nIteration;
        if (bestChangedLocal)
        {
            _best.Value.Canonicalize(_best.State);
            _bestChanged = true;
        }
    }

    public OverhaulSample CreateSample() => new(_settings);
    public void CopySample(OverhaulSample from, OverhaulSample to) => to.CopyFrom(from);

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
