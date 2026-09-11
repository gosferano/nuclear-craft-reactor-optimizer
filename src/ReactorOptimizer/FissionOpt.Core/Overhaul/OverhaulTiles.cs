namespace FissionOpt.Core.Overhaul;

/// <summary>
/// Tile IDs for the overhaul evaluator. Mirrors <c>OverhaulFission::Tiles</c>: 0–31 heat sinks,
/// 32–34 moderators, 35–36 reflectors, 37 shield, 38 irradiator, 39 conductor, 40 air, 41+ fuel
/// cells indexing <see cref="OverhaulSettings.CellTypes"/>.
/// </summary>
public static class OverhaulTiles
{
    // Heat sinks
    public const int Wt = 0, Fe = 1, Rs = 2, Qz = 3, Ob = 4, Nr = 5, Gs = 6, Lp = 7, Au = 8, Pm = 9, Sm = 10, En = 11, Pr = 12, Dm = 13, Em = 14, Cu = 15;
    public const int Sn = 16, Pb = 17, B = 18, Li = 19, Mg = 20, Mn = 21, Al = 22, Ag = 23, Fl = 24, Vi = 25, Cb = 26, As = 27, N = 28, He = 29, Ed = 30, Cr = 31;
    // Moderators
    public const int M0 = 32, M1 = 33, M2 = 34;
    // Reflectors
    public const int R0 = 35, R1 = 36;
    // Other
    public const int Shield = 37, Irradiator = 38, Conductor = 39, Air = 40, C0 = 41;

    public const int NumHeatSinks = M0;
    /// <summary>Size of <c>Settings::limits</c> / <c>Sample::limits</c>: every non-cell tile.</summary>
    public const int NumLimited = Air;

    public static readonly double[] ModeratorEfficiencies = { 1.1, 1.05, 1.0 };
    public static readonly double[] SourceEfficiencies = { 1.0, 0.95, 0.9 };
    public static readonly double[] ReflectorEfficiencies = { 0.5, 0.25 };
    public static readonly double[] ReflectorFluxMults = { 1.0, 0.5 };
    public const double SparsityPenaltyThreshold = 0.75;
    public static readonly int[] ModeratorFluxes = { 10, 22, 36 };
    public const double MaxSparsityPenaltyMult = 0.5;
    public const int CoolingEfficiencyLeniency = 10;
    public const double ShieldEfficiency = 0.5;
    public const int ShieldHeatPerFlux = 5;
    public const int NeutronReach = 4;
    public static readonly int[] CoolingRates =
    {
        55, 50, 85, 80, 70, 105, 90, 100, 110, 115, 145, 65, 95, 200, 195, 75, 120,
        60, 160, 130, 125, 150, 175, 170, 165, 180, 140, 135, 185, 190, 155, 205,
    };

    public static readonly (int dx, int dy, int dz)[] Directions =
    {
        (-1, 0, 0), (+1, 0, 0), (0, -1, 0), (0, +1, 0), (0, 0, -1), (0, 0, +1),
    };
}

public enum OverhaulGoal
{
    Output = 0,
    FuelUse = 1,
    Efficiency = 2,
    Irradiation = 3,
}

public enum TileKind : byte
{
    Air, Cell, Moderator, Reflector, Shield, Irradiator, Conductor, HeatSink,
}
