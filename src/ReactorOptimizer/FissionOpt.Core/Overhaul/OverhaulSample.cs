using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Core.Overhaul;

/// <summary>Mirrors <c>OverhaulFission::Sample</c>: design, remaining budgets and the (two) evaluations.</summary>
public sealed class OverhaulSample
{
    public int[] Limits { get; } = new int[NumLimited];
    public int[] SourceLimits { get; } = new int[3];
    /// <summary>Remaining cells per fuel (indexed like <see cref="OverhaulSettings.Fuels"/>).</summary>
    public int[] CellLimits { get; }
    public Grid3 State { get; }
    public OverhaulEvaluation Value { get; } = new();
    /// <summary>Evaluation with shields on; only initialized/run when <see cref="OverhaulSettings.Controllable"/>.</summary>
    public OverhaulEvaluation ValueWithShield { get; } = new();
    private readonly bool _controllable;

    public OverhaulSample(OverhaulSettings settings)
    {
        CellLimits = new int[settings.Fuels.Count];
        State = new Grid3(settings.SizeX, settings.SizeY, settings.SizeZ, Air);
        Value.Initialize(settings, false);
        _controllable = settings.Controllable;
        if (_controllable)
            ValueWithShield.Initialize(settings, true);
    }

    public void Evaluate()
    {
        Value.Run(State);
        if (_controllable)
            ValueWithShield.Run(State);
    }

    public void CopyFrom(OverhaulSample other)
    {
        Array.Copy(other.Limits, Limits, Limits.Length);
        Array.Copy(other.SourceLimits, SourceLimits, 3);
        Array.Copy(other.CellLimits, CellLimits, CellLimits.Length);
        State.CopyFrom(other.State);
        Value.CopyFrom(other.Value);
        if (_controllable)
            ValueWithShield.CopyFrom(other.ValueWithShield);
    }
}
