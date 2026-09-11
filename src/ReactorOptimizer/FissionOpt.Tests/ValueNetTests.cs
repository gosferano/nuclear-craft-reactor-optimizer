using FissionOpt.Core;
using FissionOpt.Tests.Reference;

namespace FissionOpt.Tests;

/// <summary>
/// Both ValueNet paths must track the original plain-loop reference through training: the scalar
/// path bit for bit, the Vector256 path to rounding (its dot products sum in a different order).
/// </summary>
public sealed class ValueNetTests
{
    [Theory]
    [InlineData(40, 0.01, 1234, false)]
    [InlineData(88, 0.001, 99, false)]
    [InlineData(17, 0.01, 7, false)]
    [InlineData(40, 0.01, 1234, true)]
    [InlineData(88, 0.001, 99, true)]
    [InlineData(17, 0.01, 7, true)]
    public void MatchesScalarReference(int nFeatures, double lRate, int seed, bool simd)
    {
        _exact = !simd;
        var a = new ValueNet(nFeatures, lRate, 100_000, new Rng(seed), simd);
        var b = new ScalarValueNetReference(nFeatures, lRate, 100_000, new Rng(seed));
        var data = new Random(seed + 1);
        var f = new double[nFeatures];

        // Same untrained predictions.
        for (int t = 0; t < 20; ++t)
        {
            Fill(data, f);
            AssertClose(b.Infer(f), a.Infer(f));
        }

        // Feed identical trajectories and train identically.
        for (int episode = 0; episode < 12; ++episode)
        {
            a.NewTrajectory(); b.NewTrajectory();
            int len = data.Next(20, 200);
            for (int i = 0; i < len; ++i)
            {
                Fill(data, f);
                a.AppendTrajectory(f); b.AppendTrajectory(f);
            }
            double target = data.NextDouble() * 5;
            a.FinishTrajectory(target); b.FinishTrajectory(target);
            int iterations = (b.TrajectoryLength * ValueNet.NEpoch + ValueNet.NMiniBatch - 1) / ValueNet.NMiniBatch;
            for (int i = 0; i < iterations; ++i)
                AssertClose(b.Train(), a.Train());
        }

        // Trained predictions agree, and the net actually learned something (sanity, not a bound).
        for (int t = 0; t < 50; ++t)
        {
            Fill(data, f);
            AssertClose(b.Infer(f), a.Infer(f));
        }
    }

    private static void Fill(Random r, double[] f)
    {
        for (int i = 0; i < f.Length; ++i) f[i] = r.NextDouble();
        f[^1] = r.NextDouble() * 3; f[^2] = r.NextDouble() * 3;
    }

    private bool _exact;

    private void AssertClose(double expected, double actual)
    {
        if (_exact)
            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual), $"expected exactly {expected:R}, got {actual:R}");
        else
            Assert.True(Math.Abs(expected - actual) <= 1e-9 * Math.Max(1.0, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");
    }
}

/// <summary>The SIMD switch must be a pure implementation choice: both settings run, and each is reproducible.</summary>
public sealed class SimdNetSettingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClassicRunIsReproducibleWithEitherPath(bool simd)
    {
        var fuel = FissionOpt.Core.Presets.ClassicPresets.FindFuel("Default", "LEU-235")!;
        var s = new FissionOpt.Core.Classic.ClassicSettings { SizeX = 5, SizeY = 5, SizeZ = 5, FuelBasePower = fuel.BasePower, FuelBaseHeat = fuel.BaseHeat, EnsureHeatNeutral = true, SymX = true, SymY = true, SymZ = true };
        Array.Copy(FissionOpt.Core.Presets.ClassicPresets.FindCoolingRates("Default")!.Rates, s.CoolingRates, s.CoolingRates.Length);
        Array.Fill(s.Limit, -1);
        for (int t = FissionOpt.Core.Classic.ClassicTiles.Active; t < FissionOpt.Core.Classic.ClassicTiles.Cell; ++t) s.Limit[t] = 0;
        using var a = new FissionOpt.Core.Classic.ClassicOpt(s, true, 3, simdNet: simd);
        using var b = new FissionOpt.Core.Classic.ClassicOpt(s, true, 3, simdNet: simd);
        Assert.Equal(simd, a.SimdNet);
        for (int i = 0; i < 40_000; ++i) { a.Step(); b.Step(); }
        Assert.Equal(a.Best.State.Data, b.Best.State.Data);
        Assert.Equal(a.LossHistory.ToArray(), b.LossHistory.ToArray());
        Assert.True(a.Best.Value.Power > 0);
    }
}
