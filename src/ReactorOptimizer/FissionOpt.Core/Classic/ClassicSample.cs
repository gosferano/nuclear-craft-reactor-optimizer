using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>Mirrors <c>Fission::Sample</c>: a candidate design, its remaining per-tile budget and its evaluation.</summary>
public sealed class ClassicSample
{
    /// <summary>Remaining budget per tile ID (negative = unlimited). Kept consistent with <see cref="State"/> under symmetry.</summary>
    public int[] Limit { get; } = new int[NumPlaceable];
    public Grid3 State { get; }
    public ClassicEvaluation Value { get; } = new();

    public ClassicSample(int sizeX, int sizeY, int sizeZ)
    {
        State = new Grid3(sizeX, sizeY, sizeZ, Air);
    }

    public void CopyFrom(ClassicSample other)
    {
        Array.Copy(other.Limit, Limit, Limit.Length);
        State.CopyFrom(other.State);
        Value.CopyFrom(other.Value);
    }

    public ClassicSample Clone()
    {
        var s = new ClassicSample(State.SizeX, State.SizeY, State.SizeZ);
        s.CopyFrom(this);
        return s;
    }
}
