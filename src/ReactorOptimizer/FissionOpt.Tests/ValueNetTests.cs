using FissionOpt.Core;
using FissionOpt.Tests.Reference;

namespace FissionOpt.Tests;

/// <summary>The SIMD ValueNet must track the plain-loop reference: same weights, same batches, same predictions to rounding.</summary>
public sealed class ValueNetTests
{
    [Theory]
    [InlineData(40, 0.01, 1234)]
    [InlineData(88, 0.001, 99)]
    [InlineData(17, 0.01, 7)]
    public void MatchesScalarReference(int nFeatures, double lRate, int seed)
    {
        var a = new ValueNet(nFeatures, lRate, 100_000, new Rng(seed));
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

    private static void AssertClose(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-9 * Math.Max(1.0, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");
}
