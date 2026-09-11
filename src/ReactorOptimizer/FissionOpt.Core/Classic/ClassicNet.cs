using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>
/// Port of <c>Fission::Net</c> (FissionNet.cpp): a hand-rolled 128→64→1 MLP with a leaky clipped
/// activation (<c>0.1x + clip(x, -1, 1)</c>) trained with Adam on (features, episode return) pairs.
/// All tensors are flat row-major arrays and every hot path is a plain loop.
/// </summary>
public sealed class ClassicNet
{
    public const int NStatisticalFeatures = 5;
    public const int NLayer1 = 128;
    public const int NLayer2 = 64;
    public const int NMiniBatch = 64;
    public const int NEpoch = 2;
    public const int NPool = 1_000_000;
    public const double LRate = 0.01, MRate = 0.9, RRate = 0.999, Leak = 0.1;

    private readonly ClassicSettings _settings;
    private readonly Rng _rng;
    private double _mCorrector = 1, _rCorrector = 1;

    // tile ID -> feature index, or -1 when the tile is disabled (limit == 0). Air is always last.
    private readonly int[] _tileToFeature = new int[Air + 1];
    private readonly int _nTiles;
    private readonly int _nFeatures;
    public int NFeatures => _nFeatures;

    // Data pool: ring buffer of feature vectors and their targets, grown on demand up to NPool.
    private double[] _poolFeatures;
    private double[] _poolTargets;
    private int _poolSize, _trajectoryLength, _writePos;

    // Parameters, Adam first/second moments.
    private readonly double[] _w1, _mw1, _rw1, _b1, _mb1, _rb1;
    private readonly double[] _w2, _mw2, _rw2, _b2, _mb2, _rb2;
    private readonly double[] _wOut, _mwOut, _rwOut;
    private double _bOut, _mbOut, _rbOut;

    // Scratch for infer.
    private readonly double[] _x, _v1, _p1, _v2, _p2;
    // Scratch for train.
    private readonly double[] _batchInput, _batchTarget, _bv1, _bp1, _bv2, _bp2, _bOutV;
    private readonly double[] _gOut, _gwOut, _gv2, _gb2, _gw2, _gv1, _gb1, _gw1;

    public ClassicNet(ClassicSettings settings, Rng rng)
    {
        _settings = settings;
        _rng = rng;
        Array.Fill(_tileToFeature, -1);
        int n = 0;
        for (int i = 0; i < Air; ++i)
            if (settings.Limit[i] != 0)
                _tileToFeature[i] = n++;
        _tileToFeature[Air] = n++;
        _nTiles = n;
        _nFeatures = _nTiles * 2 - 1 + NStatisticalFeatures;

        int cap = 1024;
        _poolFeatures = new double[cap * _nFeatures];
        _poolTargets = new double[cap];

        _w1 = new double[NLayer1 * _nFeatures]; _mw1 = new double[_w1.Length]; _rw1 = new double[_w1.Length];
        _b1 = new double[NLayer1]; _mb1 = new double[NLayer1]; _rb1 = new double[NLayer1];
        _w2 = new double[NLayer2 * NLayer1]; _mw2 = new double[_w2.Length]; _rw2 = new double[_w2.Length];
        _b2 = new double[NLayer2]; _mb2 = new double[NLayer2]; _rb2 = new double[NLayer2];
        _wOut = new double[NLayer2]; _mwOut = new double[NLayer2]; _rwOut = new double[NLayer2];
        for (int i = 0; i < _w1.Length; ++i) _w1[i] = rng.NextGaussian(0.0, 1.0 / Math.Sqrt(_nFeatures));
        for (int i = 0; i < _w2.Length; ++i) _w2[i] = rng.NextGaussian(0.0, 1.0 / Math.Sqrt(NLayer1));
        for (int i = 0; i < _wOut.Length; ++i) _wOut[i] = rng.NextGaussian(0.0, 1.0 / Math.Sqrt(NLayer2));

        _x = new double[_nFeatures];
        _v1 = new double[NLayer1]; _p1 = new double[NLayer1];
        _v2 = new double[NLayer2]; _p2 = new double[NLayer2];

        _batchInput = new double[NMiniBatch * _nFeatures];
        _batchTarget = new double[NMiniBatch];
        _bv1 = new double[NMiniBatch * NLayer1]; _bp1 = new double[_bv1.Length];
        _bv2 = new double[NMiniBatch * NLayer2]; _bp2 = new double[_bv2.Length];
        _bOutV = new double[NMiniBatch];
        _gOut = new double[NMiniBatch];
        _gwOut = new double[NLayer2];
        _gv2 = new double[NMiniBatch * NLayer2];
        _gb2 = new double[NLayer2];
        _gw2 = new double[NLayer2 * NLayer1];
        _gv1 = new double[NMiniBatch * NLayer1];
        _gb1 = new double[NLayer1];
        _gw1 = new double[NLayer1 * _nFeatures];
    }

    public int TrajectoryLength => _trajectoryLength;
    public int PoolSize => _poolSize;

    public void NewTrajectory() => _trajectoryLength = 0;

    /// <summary>Mirrors <c>Net::appendTrajectory</c>: pushes the sample's features into the ring pool with a placeholder target.</summary>
    public void AppendTrajectory(ClassicSample sample)
    {
        ++_trajectoryLength;
        if (_poolSize < NPool)
        {
            // Not full yet: _writePos == _poolSize, append (pool.emplace_back(features, 0.0)).
            if (_poolSize == _poolTargets.Length)
            {
                int cap = Math.Min(NPool, _poolTargets.Length * 2);
                Array.Resize(ref _poolFeatures, cap * _nFeatures);
                Array.Resize(ref _poolTargets, cap);
            }
            _poolTargets[_poolSize] = 0.0;
            ++_poolSize;
        }
        // Full: overwrite the features at _writePos; the old target stays until FinishTrajectory relabels it.
        ExtractFeatures(sample, _poolFeatures, _writePos * _nFeatures);
        if (++_writePos == NPool)
            _writePos = 0;
    }

    /// <summary>Mirrors <c>Net::finishTrajectory</c>: labels the last <see cref="TrajectoryLength"/> pool entries with the episode's return.</summary>
    public void FinishTrajectory(double target)
    {
        int pos = _writePos;
        for (int i = 0; i < _trajectoryLength; ++i)
        {
            if (--pos < 0)
                pos = NPool - 1;
            _poolTargets[pos] = target;
        }
    }

    /// <summary>Mirrors <c>Net::extractFeatures</c>, writing into <paramref name="dest"/> at <paramref name="offset"/>.</summary>
    public void ExtractFeatures(ClassicSample sample, double[] dest, int offset)
    {
        int nf = _nFeatures;
        Array.Clear(dest, offset, nf);
        var s = sample.State.Data;
        for (int i = 0; i < s.Length; ++i)
            dest[offset + _tileToFeature[s[i]]] += 1.0;
        var invalid = sample.Value.InvalidTiles;
        for (int i = 0; i < invalid.Count; ++i)
            dest[offset + _nTiles + _tileToFeature[sample.State[invalid[i]]]] += 1.0;
        var v = sample.Value;
        dest[offset + nf - 1] = v.PowerMult;
        dest[offset + nf - 2] = v.HeatMult;
        dest[offset + nf - 3] = v.Cooling / _settings.FuelBaseHeat;
        double volume = _settings.SizeX * _settings.SizeY * _settings.SizeZ;
        for (int i = 0; i < nf; ++i)
            dest[offset + i] /= volume;
        dest[offset + nf - 4] = v.DutyCycle;
        dest[offset + nf - 5] = v.Efficiency;
    }

    private static double Pwl(double v) => v * Leak + Math.Clamp(v, -1.0, 1.0);

    /// <summary>Mirrors <c>Net::infer</c>.</summary>
    public double Infer(ClassicSample sample)
    {
        ExtractFeatures(sample, _x, 0);
        int nf = _nFeatures;
        for (int j = 0; j < NLayer1; ++j)
        {
            double acc = _b1[j];
            int row = j * nf;
            for (int k = 0; k < nf; ++k) acc += _w1[row + k] * _x[k];
            _v1[j] = acc;
            _p1[j] = Pwl(acc);
        }
        for (int j = 0; j < NLayer2; ++j)
        {
            double acc = _b2[j];
            int row = j * NLayer1;
            for (int k = 0; k < NLayer1; ++k) acc += _w2[row + k] * _p1[k];
            _v2[j] = acc;
            _p2[j] = Pwl(acc);
        }
        double result = _bOut;
        for (int j = 0; j < NLayer2; ++j) result += _wOut[j] * _p2[j];
        return result;
    }

    /// <summary>Mirrors <c>Net::train</c>: one Adam step on a random minibatch from the pool. Returns the batch MSE.</summary>
    public double Train()
    {
        int nf = _nFeatures;
        // Assemble batch (uniform with replacement over the pool).
        for (int i = 0; i < NMiniBatch; ++i)
        {
            int p = _rng.NextInt(_poolSize - 1);
            Array.Copy(_poolFeatures, p * nf, _batchInput, i * nf, nf);
            _batchTarget[i] = _poolTargets[p];
        }

        // Forward
        for (int i = 0; i < NMiniBatch; ++i)
        {
            int xi = i * nf;
            for (int j = 0; j < NLayer1; ++j)
            {
                double acc = _b1[j];
                int row = j * nf;
                for (int k = 0; k < nf; ++k) acc += _w1[row + k] * _batchInput[xi + k];
                _bv1[i * NLayer1 + j] = acc;
                _bp1[i * NLayer1 + j] = Pwl(acc);
            }
            for (int j = 0; j < NLayer2; ++j)
            {
                double acc = _b2[j];
                int row = j * NLayer1;
                int pi = i * NLayer1;
                for (int k = 0; k < NLayer1; ++k) acc += _w2[row + k] * _bp1[pi + k];
                _bv2[i * NLayer2 + j] = acc;
                _bp2[i * NLayer2 + j] = Pwl(acc);
            }
            double o = _bOut;
            for (int j = 0; j < NLayer2; ++j) o += _wOut[j] * _bp2[i * NLayer2 + j];
            _bOutV[i] = o;
        }
        double loss = 0.0;
        for (int i = 0; i < NMiniBatch; ++i)
        {
            double d = _bOutV[i] - _batchTarget[i];
            loss += d * d;
        }
        loss /= NMiniBatch;

        // Backward
        double gbOut = 0.0;
        for (int i = 0; i < NMiniBatch; ++i)
        {
            _gOut[i] = (_bOutV[i] - _batchTarget[i]) * 2 / NMiniBatch;
            gbOut += _gOut[i];
        }
        for (int j = 0; j < NLayer2; ++j)
        {
            double acc = 0.0;
            for (int i = 0; i < NMiniBatch; ++i) acc += _gOut[i] * _bp2[i * NLayer2 + j];
            _gwOut[j] = acc;
        }
        // gvLayer2 = gvPwlLayer2 * (leak + (|vLayer2| < 1)), gvPwlLayer2(i,j) = gvOutput(i) * wOutput(j)
        for (int i = 0; i < NMiniBatch; ++i)
            for (int j = 0; j < NLayer2; ++j)
            {
                int idx = i * NLayer2 + j;
                _gv2[idx] = _gOut[i] * _wOut[j] * (Leak + (Math.Abs(_bv2[idx]) < 1.0 ? 1.0 : 0.0));
            }
        for (int j = 0; j < NLayer2; ++j)
        {
            double acc = 0.0;
            for (int i = 0; i < NMiniBatch; ++i) acc += _gv2[i * NLayer2 + j];
            _gb2[j] = acc;
        }
        for (int j = 0; j < NLayer2; ++j)
            for (int k = 0; k < NLayer1; ++k)
            {
                double acc = 0.0;
                for (int i = 0; i < NMiniBatch; ++i) acc += _gv2[i * NLayer2 + j] * _bp1[i * NLayer1 + k];
                _gw2[j * NLayer1 + k] = acc;
            }
        // gvPwlLayer1(i,k) = sum_j gvLayer2(i,j) * wLayer2(j,k); gvLayer1 = that * (leak + (|vLayer1| < 1))
        for (int i = 0; i < NMiniBatch; ++i)
            for (int k = 0; k < NLayer1; ++k)
            {
                double acc = 0.0;
                for (int j = 0; j < NLayer2; ++j) acc += _gv2[i * NLayer2 + j] * _w2[j * NLayer1 + k];
                int idx = i * NLayer1 + k;
                _gv1[idx] = acc * (Leak + (Math.Abs(_bv1[idx]) < 1.0 ? 1.0 : 0.0));
            }
        for (int k = 0; k < NLayer1; ++k)
        {
            double acc = 0.0;
            for (int i = 0; i < NMiniBatch; ++i) acc += _gv1[i * NLayer1 + k];
            _gb1[k] = acc;
        }
        for (int k = 0; k < NLayer1; ++k)
            for (int m = 0; m < nf; ++m)
            {
                double acc = 0.0;
                for (int i = 0; i < NMiniBatch; ++i) acc += _gv1[i * NLayer1 + k] * _batchInput[i * nf + m];
                _gw1[k * nf + m] = acc;
            }

        // Adam
        _mCorrector *= MRate;
        _rCorrector *= RRate;
        Adam(_w1, _mw1, _rw1, _gw1);
        Adam(_b1, _mb1, _rb1, _gb1);
        Adam(_w2, _mw2, _rw2, _gw2);
        Adam(_b2, _mb2, _rb2, _gb2);
        Adam(_wOut, _mwOut, _rwOut, _gwOut);
        _mbOut = MRate * _mbOut + (1 - MRate) * gbOut;
        _rbOut = RRate * _rbOut + (1 - RRate) * (gbOut * gbOut);
        _bOut -= LRate * _mbOut / ((1 - _mCorrector) * (Math.Sqrt(_rbOut / (1 - _rCorrector)) + 1e-8));

        return loss;
    }

    private void Adam(double[] w, double[] m, double[] r, double[] g)
    {
        double mc = 1 - _mCorrector, rc = 1 - _rCorrector;
        for (int i = 0; i < w.Length; ++i)
        {
            m[i] = MRate * m[i] + (1 - MRate) * g[i];
            r[i] = RRate * r[i] + (1 - RRate) * (g[i] * g[i]);
            w[i] -= LRate * m[i] / (mc * (Math.Sqrt(r[i] / rc) + 1e-8));
        }
    }
}
