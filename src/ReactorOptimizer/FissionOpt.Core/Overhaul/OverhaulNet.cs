using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Core.Overhaul;

/// <summary>
/// Port of <c>OverhaulFission::Net</c> (OverhaulFissionNet.cpp): the overhaul feature extractor over
/// a shared <see cref="ValueNet"/>. Features: counts per non-cell tile type, counts of functional
/// tiles per type (from the evaluation), and eight statistics.
/// </summary>
public sealed class OverhaulNet
{
    public const int NStatisticalFeatures = 8;
    public const int NPool = 10_000_000;
    public const double LRate = 0.001;
    public const int NMiniBatch = ValueNet.NMiniBatch;
    public const int NEpoch = ValueNet.NEpoch;

    private readonly OverhaulSettings _settings;
    private readonly ValueNet _net;
    private readonly int[] _tileToFeature = new int[Air + 1];
    private readonly int _nTiles;
    private readonly double[] _features;

    public OverhaulNet(OverhaulSettings settings, Rng rng, bool simd = true)
    {
        _settings = settings;
        Array.Fill(_tileToFeature, -1);
        int n = 0;
        for (int i = 0; i < Air; ++i)
            if (settings.Limits[i] != 0)
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
    public void AppendTrajectory(ReadOnlySpan<double> features) => _net.AppendTrajectory(features);

    public void AppendTrajectory(OverhaulSample sample)
    {
        ExtractFeatures(sample, _features);
        _net.AppendTrajectory(_features);
    }

    public double Infer(OverhaulSample sample)
    {
        ExtractFeatures(sample, _features);
        return _net.Infer(_features);
    }

    /// <summary>Mirrors <c>Net::extractFeatures</c>. Cells (tile &gt; Air) contribute only through the statistics.</summary>
    public void ExtractFeatures(OverhaulSample sample, double[] dest)
    {
        int nf = dest.Length;
        Array.Clear(dest);
        var s = sample.State.Data;
        var v = sample.Value;
        for (int i = 0; i < s.Length; ++i)
        {
            int tile = s[i];
            if (tile > Air)
                continue;
            int index = _tileToFeature[tile];
            dest[index] += 1.0;
            if (tile == Air)
                continue;
            bool isFunctional = v.KindAt(i) switch
            {
                TileKind.Moderator => v.IsFunctionalAt(i),
                TileKind.Reflector => v.IsActiveAt(i),
                TileKind.Shield => v.FluxAt(i) != 0,
                TileKind.Irradiator => v.FluxAt(i) != 0,
                TileKind.HeatSink => v.IsActiveAt(i),
                TileKind.Conductor => v.ClusterAt(i) >= 0,
                _ => throw new InvalidOperationException("tile/evaluation mismatch"),
            };
            if (isFunctional)
                dest[_nTiles + index] += 1.0;
        }
        dest[nf - 8] = v.Cells.Count;
        dest[nf - 7] = v.NActiveCells;
        dest[nf - 6] = v.ClusterCount;
        double volume = _settings.SizeX * _settings.SizeY * _settings.SizeZ;
        for (int i = 0; i < nf; ++i)
            dest[i] /= volume;
        dest[nf - 5] = (double)v.TotalRawFlux / _settings.MinCriticality;
        dest[nf - 4] = (double)v.TotalPositiveNetHeat / _settings.MinHeat;
        dest[nf - 3] = v.Output / _settings.MaxOutput;
        dest[nf - 2] = v.Efficiency;
        dest[nf - 1] = (double)v.IrradiatorFlux / _settings.MinCriticality;
    }
}
