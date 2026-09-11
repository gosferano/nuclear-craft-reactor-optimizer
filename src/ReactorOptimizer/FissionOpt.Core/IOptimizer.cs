namespace FissionOpt.Core;

/// <summary>Common surface of the classic and overhaul optimizers, as consumed by <see cref="OptimizerRunner{TSample}"/>.</summary>
public interface IOptimizer<TSample> where TSample : class
{
    void Step();
    bool NeedsRedrawBest();
    bool NeedsReplotLoss();
    TSample Best { get; }
    ReadOnlySpan<double> LossHistory { get; }
    int NEpisode { get; }
    int NStage { get; }
    int NIteration { get; }
    int Seed { get; }
    /// <summary>Creates an empty sample of the right shape for snapshots.</summary>
    TSample CreateSample();
    /// <summary>Deep-copies <paramref name="from"/> into <paramref name="to"/>.</summary>
    void CopySample(TSample from, TSample to);
}
