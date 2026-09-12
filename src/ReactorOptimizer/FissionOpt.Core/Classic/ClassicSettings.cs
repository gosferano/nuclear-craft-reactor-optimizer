namespace FissionOpt.Core.Classic;

/// <summary>Mirrors <c>Fission::Settings</c>.</summary>
public sealed class ClassicSettings
{
    public int SizeX { get; set; }
    public int SizeY { get; set; }
    public int SizeZ { get; set; }
    public double FuelBasePower { get; set; }
    public double FuelBaseHeat { get; set; }

    /// <summary>Per-tile budget, indexed by tile ID 0..31. Negative means unlimited.</summary>
    public int[] Limit { get; } = new int[ClassicTiles.NumPlaceable];

    /// <summary>Cooling rate per cooler tile ID 0..29 (passive then active).</summary>
    public double[] CoolingRates { get; } = new double[ClassicTiles.NumCoolerIds];

    public bool EnsureActiveCoolerAccessible { get; set; }
    public bool EnsureHeatNeutral { get; set; }
    public ClassicGoal Goal { get; set; }
    public bool SymX { get; set; }
    public bool SymY { get; set; }
    public bool SymZ { get; set; }

    /// <summary>
    /// Evaluate the grid as a torus: neighbours and moderator rays wrap around and there is no casing.
    /// Not an in-game configuration — used to optimize bulk unit designs for tiled restarts, where a
    /// unit is surrounded by copies of itself rather than by walls. Casing-dependent coolers are
    /// invalid everywhere on a torus by their own rule; active-cooler accessibility is not modelled.
    /// </summary>
    public bool Periodic { get; set; }

    /// <summary>
    /// Tie-break weight for cooling surplus in the fitness: adds <c>weight × (cooling − heat) / (maxRate × volume)</c>,
    /// a term bounded by ±weight, so it never outranks a real change in the goal metric but decides between
    /// otherwise-equal designs. Used for unit designs, whose surplus is what the big-box search later spends;
    /// 0 (default) is upstream's fitness exactly.
    /// </summary>
    public double SurplusTieBreak { get; set; }

    public int Volume => SizeX * SizeY * SizeZ;

    public ClassicSettings Clone()
    {
        var s = new ClassicSettings
        {
            SizeX = SizeX, SizeY = SizeY, SizeZ = SizeZ,
            FuelBasePower = FuelBasePower, FuelBaseHeat = FuelBaseHeat,
            EnsureActiveCoolerAccessible = EnsureActiveCoolerAccessible,
            EnsureHeatNeutral = EnsureHeatNeutral,
            Goal = Goal, SymX = SymX, SymY = SymY, SymZ = SymZ, Periodic = Periodic, SurplusTieBreak = SurplusTieBreak,
        };
        Array.Copy(Limit, s.Limit, Limit.Length);
        Array.Copy(CoolingRates, s.CoolingRates, CoolingRates.Length);
        return s;
    }
}
