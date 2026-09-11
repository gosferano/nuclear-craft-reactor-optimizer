using System.Diagnostics;
using FissionOpt.Core;
using FissionOpt.Tests.Reference;
using Xunit.Abstractions;

namespace FissionOpt.Tests;

/// <summary>Not an assertion of speed — prints the per-call cost of both implementations so regressions are visible in the test log.</summary>
public sealed class ValueNetBench
{
    private readonly ITestOutputHelper _o;
    public ValueNetBench(ITestOutputHelper o) => _o = o;

    [Fact]
    public void PrintTimings()
    {
        const int nf = 40;
        var a = new ValueNet(nf, 0.01, 100_000, new Rng(1), simd: true);
        var b = new ValueNet(nf, 0.01, 100_000, new Rng(1), simd: false);
        var f = new double[nf];
        var r = new Random(2);
        for (int i = 0; i < 2000; ++i) { for (int k = 0; k < nf; ++k) f[k] = r.NextDouble(); a.AppendTrajectory(f); b.AppendTrajectory(f); }
        a.FinishTrajectory(1); b.FinishTrajectory(1);
        for (int i = 0; i < 50; ++i) { a.Train(); b.Train(); }
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 500; ++i) a.Train();
        double tA = sw.Elapsed.TotalMilliseconds / 500;
        sw.Restart();
        for (int i = 0; i < 500; ++i) b.Train();
        double tB = sw.Elapsed.TotalMilliseconds / 500;
        sw.Restart();
        double acc = 0;
        for (int i = 0; i < 200_000; ++i) acc += a.Infer(f);
        double iA = sw.Elapsed.TotalMilliseconds * 1000 / 200_000;
        sw.Restart();
        for (int i = 0; i < 200_000; ++i) acc += b.Infer(f);
        double iB = sw.Elapsed.TotalMilliseconds * 1000 / 200_000;
        _o.WriteLine($"train: simd {tA:F3} ms, scalar {tB:F3} ms ({tB / tA:F1}x); infer: simd {iA:F2} us, scalar {iB:F2} us ({iB / iA:F1}x) [{acc:0}]");
    }
}
