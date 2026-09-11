namespace FissionOpt.Core.Overhaul;

/// <summary>Mirrors <c>OverhaulFission::Fuel</c>.</summary>
public sealed class OverhaulFuel
{
    public double Efficiency;
    /// <summary>Max cells of this fuel; negative = unlimited.</summary>
    public int Limit;
    public int Criticality;
    public int Heat;
    public bool SelfPriming;

    public OverhaulFuel() { }

    public OverhaulFuel(double efficiency, int limit, int criticality, int heat, bool selfPriming)
    {
        Efficiency = efficiency; Limit = limit; Criticality = criticality; Heat = heat; SelfPriming = selfPriming;
    }
}

/// <summary>Mirrors <c>OverhaulFission::Settings</c>. Call <see cref="Compute"/> after filling in the fuels.</summary>
public sealed class OverhaulSettings
{
    public int SizeX, SizeY, SizeZ;
    public List<OverhaulFuel> Fuels { get; } = new();
    /// <summary>Per-tile budget for the non-cell tiles (0..39). Negative = unlimited.</summary>
    public int[] Limits { get; } = new int[OverhaulTiles.NumLimited];
    /// <summary>Budget per neutron-source type (1..3). Negative = unlimited.</summary>
    public int[] SourceLimits { get; } = new int[3];
    public OverhaulGoal Goal;
    public bool Controllable;
    public bool SymX, SymY, SymZ;

    // Computed
    /// <summary>One entry per cell tile ID (offset from C0): (fuel index, neutron source 0..3).</summary>
    public List<(int fuel, int source)> CellTypes { get; } = new();
    public double MaxOutput;
    public int MinCriticality;
    public int MinHeat;

    public int Volume => SizeX * SizeY * SizeZ;

    /// <summary>Mirrors <c>Settings::compute</c>: expands each fuel into one cell type per neutron source unless self-priming.</summary>
    public void Compute()
    {
        CellTypes.Clear();
        MaxOutput = 0.0;
        MinCriticality = int.MaxValue;
        MinHeat = int.MaxValue;
        for (int i = 0; i < Fuels.Count; ++i)
        {
            MaxOutput = Math.Max(MaxOutput, Fuels[i].Heat * Fuels[i].Efficiency);
            MinCriticality = Math.Min(MinCriticality, Fuels[i].Criticality);
            MinHeat = Math.Min(MinHeat, Fuels[i].Heat);
            CellTypes.Add((i, 0));
            if (!Fuels[i].SelfPriming)
                for (int j = 1; j <= 3; ++j)
                    CellTypes.Add((i, j));
        }
    }
}
