using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Port of <c>Fission::Net</c> (FissionNet.cpp): the classic feature extractor over a shared
/// <see cref="ValueNet"/>. Features: per-tile-type counts, per-type invalid-tile counts, and five
/// statistics, all divided by the volume except duty cycle and efficiency.
/// </summary>
public sealed class ClassicNet
{
    public const int NStatisticalFeatures = 5;
    public const int NPool = 1_000_000;
    public const double LRate = 0.01;
    public const int NMiniBatch = ValueNet.NMiniBatch;
    public const int NEpoch = ValueNet.NEpoch;

    private readonly ClassicSettings _settings;
    private readonly ValueNet _net;
    // tile ID -> feature index, or -1 when the tile is disabled (limit == 0). Air is always last.
    private readonly int[] _tileToFeature = new int[Air + 1];
    private readonly int _nTiles;
    private readonly double[] _features;

    public ClassicNet(ClassicSettings settings, Rng rng, bool simd = true)
    {
        _settings = settings;
        Array.Fill(_tileToFeature, -1);
        int n = 0;
        for (int i = 0; i < Air; ++i)
            if (settings.Limit[i] != 0)
                _tileToFeature[i] = n++;
        _tileToFeature[Air] = n++;
        _nTiles = n;
        int nFeatures = _nTiles * 2 - 1 + NStatisticalFeatures;
        _features = new double[nFeatures];
        _net = new ValueNet(nFeatures, LRate, NPool, rng, simd);
    }

    public int NFeatures => _net.NFeatures;
    public bool Simd => _net.Simd;
    public int TrajectoryLength => _net.TrajectoryLength;
    public void NewTrajectory() => _net.NewTrajectory();
    public void FinishTrajectory(double target) => _net.FinishTrajectory(target);
    public double Train() => _net.Train();

    public void AppendTrajectory(ClassicSample sample)
    {
        ExtractFeatures(sample, _features);
        _net.AppendTrajectory(_features);
    }

    public double Infer(ClassicSample sample)
    {
        ExtractFeatures(sample, _features);
        return _net.Infer(_features);
    }

    public void AppendTrajectory(ReadOnlySpan<int> countByTile, ReadOnlySpan<int> invalidByTile, ClassicEvaluation value)
    {
        ExtractFeatures(countByTile, invalidByTile, value, _features);
        _net.AppendTrajectory(_features);
    }

    public double Infer(ReadOnlySpan<int> countByTile, ReadOnlySpan<int> invalidByTile, ClassicEvaluation value)
    {
        ExtractFeatures(countByTile, invalidByTile, value, _features);
        return _net.Infer(_features);
    }

    /// <summary>Same features as <see cref="ExtractFeatures(ClassicSample, double[])"/>, from per-tile-ID counts instead of a materialized grid.</summary>
    public void ExtractFeatures(ReadOnlySpan<int> countByTile, ReadOnlySpan<int> invalidByTile, ClassicEvaluation v, double[] dest)
    {
        int nf = dest.Length;
        Array.Clear(dest);
        for (int t = 0; t <= Air; ++t)
        {
            int f = _tileToFeature[t];
            if (f < 0) continue;
            dest[f] += countByTile[t];
            if (t != Air) dest[_nTiles + f] += invalidByTile[t];
        }
        dest[nf - 1] = v.PowerMult;
        dest[nf - 2] = v.HeatMult;
        dest[nf - 3] = v.Cooling / _settings.FuelBaseHeat;
        double volume = _settings.SizeX * _settings.SizeY * _settings.SizeZ;
        for (int i = 0; i < nf; ++i)
            dest[i] /= volume;
        dest[nf - 4] = v.DutyCycle;
        dest[nf - 5] = v.Efficiency;
    }

    /// <summary>Mirrors <c>Net::extractFeatures</c>.</summary>
    public void ExtractFeatures(ClassicSample sample, double[] dest)
    {
        int nf = dest.Length;
        Array.Clear(dest);
        var s = sample.State.Data;
        for (int i = 0; i < s.Length; ++i)
            dest[_tileToFeature[s[i]]] += 1.0;
        var invalid = sample.Value.InvalidTiles;
        for (int i = 0; i < invalid.Count; ++i)
            dest[_nTiles + _tileToFeature[sample.State[invalid[i]]]] += 1.0;
        var v = sample.Value;
        dest[nf - 1] = v.PowerMult;
        dest[nf - 2] = v.HeatMult;
        dest[nf - 3] = v.Cooling / _settings.FuelBaseHeat;
        double volume = _settings.SizeX * _settings.SizeY * _settings.SizeZ;
        for (int i = 0; i < nf; ++i)
            dest[i] /= volume;
        dest[nf - 4] = v.DutyCycle;
        dest[nf - 5] = v.Efficiency;
    }
}
