using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace FissionOpt.Core;

/// <summary>
/// The value network shared by both optimizers (FissionNet.cpp / OverhaulFissionNet.cpp): a
/// 128→64→1 MLP with the leaky clipped activation <c>0.1x + clip(x, -1, 1)</c>, trained with Adam
/// on minibatches drawn from a ring pool of (features, episode return) pairs. Feature extraction is
/// the caller's job. Nothing allocates after construction except the pool growing towards its cap.
///
/// Two arithmetic paths, chosen per instance:
/// <list type="bullet">
/// <item><b>SIMD</b> (<see cref="Simd"/> = true): hand-written <see cref="Vector256{T}"/> kernels. The
/// shape is fixed at four lanes and the reduction order is spelled out in the code, so results are
/// bit-identical on every CPU (AVX-512 machines use 256-bit registers; ARM emulates two 128-bit halves).</item>
/// <item><b>Scalar</b> (false): the original plain loops. Differs from the SIMD path only in the summation
/// order of the layer dot products, i.e. in the last bits.</item>
/// </list>
/// A seeded run therefore reproduces exactly on any machine as long as the same path is used.
/// </summary>
public sealed class ValueNet
{
    public const int NLayer1 = 128;
    public const int NLayer2 = 64;
    public const int NMiniBatch = 64;
    public const int NEpoch = 2;
    public const double MRate = 0.9, RRate = 0.999, Leak = 0.1;

    private readonly Rng _rng;
    private readonly int _nFeatures;
    private readonly int _nPool;
    private readonly double _lRate;
    private readonly bool _simd;
    private double _mCorrector = 1, _rCorrector = 1;

    private double[] _poolFeatures;
    private double[] _poolTargets;
    private int _poolSize, _trajectoryLength, _writePos;

    private readonly double[] _w1, _mw1, _rw1, _b1, _mb1, _rb1;
    private readonly double[] _w2, _mw2, _rw2, _b2, _mb2, _rb2;
    private readonly double[] _wOut, _mwOut, _rwOut;
    private double _bOut, _mbOut, _rbOut;

    private readonly double[] _v1, _p1, _v2, _p2;
    private readonly double[] _batchInput, _batchTarget, _bv1, _bp1, _bv2, _bp2, _bOutV;
    private readonly double[] _gOut, _gwOut, _gv2, _gb2, _gw2, _gv1, _gb1, _gw1;

    public int NFeatures => _nFeatures;
    public int TrajectoryLength => _trajectoryLength;
    public int PoolSize => _poolSize;
    /// <summary>True when the <see cref="Vector256{T}"/> kernels are used.</summary>
    public bool Simd => _simd;

    public ValueNet(int nFeatures, double lRate, int nPool, Rng rng, bool simd = true)
    {
        _rng = rng;
        _nFeatures = nFeatures;
        _lRate = lRate;
        _nPool = nPool;
        _simd = simd;

        int cap = Math.Min(1024, nPool);
        _poolFeatures = new double[cap * nFeatures];
        _poolTargets = new double[cap];

        _w1 = new double[NLayer1 * nFeatures]; _mw1 = new double[_w1.Length]; _rw1 = new double[_w1.Length];
        _b1 = new double[NLayer1]; _mb1 = new double[NLayer1]; _rb1 = new double[NLayer1];
        _w2 = new double[NLayer2 * NLayer1]; _mw2 = new double[_w2.Length]; _rw2 = new double[_w2.Length];
        _b2 = new double[NLayer2]; _mb2 = new double[NLayer2]; _rb2 = new double[NLayer2];
        _wOut = new double[NLayer2]; _mwOut = new double[NLayer2]; _rwOut = new double[NLayer2];
        for (int i = 0; i < _w1.Length; ++i) _w1[i] = rng.NextGaussian(0.0, 1.0 / Math.Sqrt(nFeatures));
        for (int i = 0; i < _w2.Length; ++i) _w2[i] = rng.NextGaussian(0.0, 1.0 / Math.Sqrt(NLayer1));
        for (int i = 0; i < _wOut.Length; ++i) _wOut[i] = rng.NextGaussian(0.0, 1.0 / Math.Sqrt(NLayer2));

        _v1 = new double[NLayer1]; _p1 = new double[NLayer1];
        _v2 = new double[NLayer2]; _p2 = new double[NLayer2];
        _batchInput = new double[NMiniBatch * nFeatures];
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
        _gw1 = new double[NLayer1 * nFeatures];
    }

    public void NewTrajectory() => _trajectoryLength = 0;

    /// <summary>Mirrors <c>Net::appendTrajectory</c>: pushes a feature vector into the ring pool with a placeholder target.</summary>
    public void AppendTrajectory(ReadOnlySpan<double> features)
    {
        if (features.Length != _nFeatures) throw new ArgumentException("feature length mismatch", nameof(features));
        if (_trajectoryLength < _nPool)
            ++_trajectoryLength;
        if (_poolSize < _nPool)
        {
            // Not full yet: _writePos == _poolSize, append (pool.emplace_back(features, 0.0)).
            if (_poolSize == _poolTargets.Length)
            {
                int cap = (int)Math.Min(_nPool, (long)_poolTargets.Length * 2);
                Array.Resize(ref _poolFeatures, cap * _nFeatures);
                Array.Resize(ref _poolTargets, cap);
            }
            _poolTargets[_poolSize] = 0.0;
            ++_poolSize;
        }
        // Full: overwrite the features at _writePos; the old target stays until FinishTrajectory relabels it.
        features.CopyTo(_poolFeatures.AsSpan(_writePos * _nFeatures, _nFeatures));
        if (++_writePos == _nPool)
            _writePos = 0;
    }

    /// <summary>Mirrors <c>Net::finishTrajectory</c>: labels the last <see cref="TrajectoryLength"/> pool entries with the episode's return.</summary>
    public void FinishTrajectory(double target)
    {
        int pos = _writePos;
        for (int i = 0; i < _trajectoryLength; ++i)
        {
            if (--pos < 0)
                pos = _nPool - 1;
            _poolTargets[pos] = target;
        }
    }

    // ---------------------------------------------------------------- kernels
    // Each kernel has a Vector256 body and a scalar body performing the same IEEE operations per
    // element; only Dot's reduction order differs between the two.

    /// <summary>
    /// init + x · y. Scalar: the products are added to <paramref name="init"/> one by one, exactly like the
    /// original loops. SIMD: four lane-wise partial sums combined as init + ((l0 + l1) + l2) + l3, then the tail.
    /// </summary>
    private double Dot(double init, ReadOnlySpan<double> x, ReadOnlySpan<double> y)
    {
        int n = x.Length;
        double sum = init;
        int i = 0;
        if (_simd)
        {
            ref double rx = ref MemoryMarshal.GetReference(x);
            ref double ry = ref MemoryMarshal.GetReference(y);
            var acc = Vector256<double>.Zero;
            for (; i <= n - 4; i += 4)
                acc += Vector256.LoadUnsafe(ref rx, (nuint)i) * Vector256.LoadUnsafe(ref ry, (nuint)i);
            sum += ((acc.GetElement(0) + acc.GetElement(1)) + acc.GetElement(2)) + acc.GetElement(3);
        }
        for (; i < n; ++i)
            sum += x[i] * y[i];
        return sum;
    }

    /// <summary>dest += s · x, element-wise.</summary>
    private void Axpy(ReadOnlySpan<double> x, double s, Span<double> dest)
    {
        int n = x.Length;
        int i = 0;
        if (_simd)
        {
            ref double rx = ref MemoryMarshal.GetReference(x);
            ref double rd = ref MemoryMarshal.GetReference(dest);
            var vs = Vector256.Create(s);
            for (; i <= n - 4; i += 4)
                (Vector256.LoadUnsafe(ref rd, (nuint)i) + vs * Vector256.LoadUnsafe(ref rx, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        }
        for (; i < n; ++i)
            dest[i] += s * x[i];
    }

    /// <summary>p = v·leak + clip(v, −1, 1), element-wise.</summary>
    private void Pwl(ReadOnlySpan<double> v, Span<double> p)
    {
        int n = v.Length;
        int i = 0;
        if (_simd)
        {
            ref double rv = ref MemoryMarshal.GetReference(v);
            ref double rp = ref MemoryMarshal.GetReference(p);
            var leak = Vector256.Create(Leak);
            var lo = Vector256.Create(-1.0);
            var hi = Vector256.Create(1.0);
            for (; i <= n - 4; i += 4)
            {
                var x = Vector256.LoadUnsafe(ref rv, (nuint)i);
                (x * leak + Vector256.Min(Vector256.Max(x, lo), hi)).StoreUnsafe(ref rp, (nuint)i);
            }
        }
        for (; i < n; ++i)
            p[i] = v[i] * Leak + Math.Clamp(v[i], -1.0, 1.0);
    }

    /// <summary>Dense layer: v[j] = b[j] + w[j,:] · x.</summary>
    private void Layer(ReadOnlySpan<double> w, ReadOnlySpan<double> b, ReadOnlySpan<double> x, Span<double> v)
    {
        int n = x.Length;
        for (int j = 0; j < v.Length; ++j)
            v[j] = Dot(b[j], w.Slice(j * n, n), x);
    }

    // ---------------------------------------------------------------- inference / training

    /// <summary>Mirrors <c>Net::infer</c>.</summary>
    public double Infer(ReadOnlySpan<double> x)
    {
        Layer(_w1, _b1, x, _v1);
        Pwl(_v1, _p1);
        Layer(_w2, _b2, _p1, _v2);
        Pwl(_v2, _p2);
        return Dot(_bOut, _wOut, _p2);
    }

    /// <summary>Mirrors <c>Net::train</c>: one Adam step on a random minibatch from the pool. Returns the batch MSE.</summary>
    public double Train()
    {
        int nf = _nFeatures;
        for (int i = 0; i < NMiniBatch; ++i)
        {
            int p = _rng.NextInt(_poolSize - 1);
            Array.Copy(_poolFeatures, p * nf, _batchInput, i * nf, nf);
            _batchTarget[i] = _poolTargets[p];
        }

        // Forward
        for (int i = 0; i < NMiniBatch; ++i)
        {
            var x = _batchInput.AsSpan(i * nf, nf);
            var v1 = _bv1.AsSpan(i * NLayer1, NLayer1);
            var p1 = _bp1.AsSpan(i * NLayer1, NLayer1);
            var v2 = _bv2.AsSpan(i * NLayer2, NLayer2);
            var p2 = _bp2.AsSpan(i * NLayer2, NLayer2);
            Layer(_w1, _b1, x, v1);
            Pwl(v1, p1);
            Layer(_w2, _b2, p1, v2);
            Pwl(v2, p2);
            _bOutV[i] = Dot(_bOut, _wOut, p2);
        }
        double loss = 0.0;
        for (int i = 0; i < NMiniBatch; ++i)
        {
            double d = _bOutV[i] - _batchTarget[i];
            loss += d * d;
        }
        loss /= NMiniBatch;

        // Backward. The gradient outer products are accumulated sample by sample (axpy), which is the
        // same summation order as the reference loops.
        double gbOut = 0.0;
        for (int i = 0; i < NMiniBatch; ++i)
        {
            _gOut[i] = (_bOutV[i] - _batchTarget[i]) * 2 / NMiniBatch;
            gbOut += _gOut[i];
        }
        Array.Clear(_gwOut); Array.Clear(_gb2); Array.Clear(_gw2); Array.Clear(_gb1); Array.Clear(_gw1);
        for (int i = 0; i < NMiniBatch; ++i)
        {
            var p2 = _bp2.AsSpan(i * NLayer2, NLayer2);
            Axpy(p2, _gOut[i], _gwOut);
            // gv2 = gOut[i] * wOut ⊙ (leak + (|v2| < 1))
            var v2 = _bv2.AsSpan(i * NLayer2, NLayer2);
            var gv2 = _gv2.AsSpan(i * NLayer2, NLayer2);
            double g = _gOut[i];
            for (int j = 0; j < NLayer2; ++j)
                gv2[j] = g * _wOut[j] * (Leak + (Math.Abs(v2[j]) < 1.0 ? 1.0 : 0.0));
            Axpy(gv2, 1.0, _gb2);
            // gw2[j,:] += gv2[j] * p1 ;  gp1 = Σ_j gv2[j] * w2[j,:]
            var p1 = _bp1.AsSpan(i * NLayer1, NLayer1);
            var gp1 = _gv1.AsSpan(i * NLayer1, NLayer1);
            gp1.Clear();
            for (int j = 0; j < NLayer2; ++j)
            {
                Axpy(p1, gv2[j], _gw2.AsSpan(j * NLayer1, NLayer1));
                Axpy(_w2.AsSpan(j * NLayer1, NLayer1), gv2[j], gp1);
            }
            // gv1 = gp1 ⊙ (leak + (|v1| < 1))
            var v1 = _bv1.AsSpan(i * NLayer1, NLayer1);
            for (int k = 0; k < NLayer1; ++k)
                gp1[k] *= Leak + (Math.Abs(v1[k]) < 1.0 ? 1.0 : 0.0);
            Axpy(gp1, 1.0, _gb1);
            var x = _batchInput.AsSpan(i * nf, nf);
            for (int k = 0; k < NLayer1; ++k)
                Axpy(x, gp1[k], _gw1.AsSpan(k * nf, nf));
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
        _bOut -= _lRate * _mbOut / ((1 - _mCorrector) * (Math.Sqrt(_rbOut / (1 - _rCorrector)) + 1e-8));

        return loss;
    }

    /// <summary>
    /// Adam update, element-wise: m = β1·m + (1−β1)·g; r = β2·r + (1−β2)·g²;
    /// w −= lr·m / ((1−β1ᵗ)·(√(r/(1−β2ᵗ)) + 1e−8)). Same operation order in both paths.
    /// </summary>
    private void Adam(double[] w, double[] m, double[] r, double[] g)
    {
        int n = w.Length;
        double mc = 1 - _mCorrector, rc = 1 - _rCorrector;
        int i = 0;
        if (_simd)
        {
            ref double rw = ref MemoryMarshal.GetReference(w.AsSpan());
            ref double rm = ref MemoryMarshal.GetReference(m.AsSpan());
            ref double rr = ref MemoryMarshal.GetReference(r.AsSpan());
            ref double rg = ref MemoryMarshal.GetReference(g.AsSpan());
            var vM = Vector256.Create(MRate); var v1M = Vector256.Create(1 - MRate);
            var vR = Vector256.Create(RRate); var v1R = Vector256.Create(1 - RRate);
            var vLr = Vector256.Create(_lRate); var vMc = Vector256.Create(mc); var vRc = Vector256.Create(rc);
            var eps = Vector256.Create(1e-8);
            for (; i <= n - 4; i += 4)
            {
                var vg = Vector256.LoadUnsafe(ref rg, (nuint)i);
                var vm = vM * Vector256.LoadUnsafe(ref rm, (nuint)i) + v1M * vg;
                var vr = vR * Vector256.LoadUnsafe(ref rr, (nuint)i) + v1R * (vg * vg);
                vm.StoreUnsafe(ref rm, (nuint)i);
                vr.StoreUnsafe(ref rr, (nuint)i);
                var vw = Vector256.LoadUnsafe(ref rw, (nuint)i);
                (vw - vLr * vm / (vMc * (Vector256.Sqrt(vr / vRc) + eps))).StoreUnsafe(ref rw, (nuint)i);
            }
        }
        for (; i < n; ++i)
        {
            m[i] = MRate * m[i] + (1 - MRate) * g[i];
            r[i] = RRate * r[i] + (1 - RRate) * (g[i] * g[i]);
            w[i] -= _lRate * m[i] / (mc * (Math.Sqrt(r[i] / rc) + 1e-8));
        }
    }
}
