namespace FissionOpt.Core.Classic;

/// <summary>
/// Tile IDs for the classic (pre-overhaul) evaluator. Mirrors the anonymous enum in Fission.h.
/// 0–14 are passive coolers, 15–29 are the active-cooler variants (<c>id - Active</c> gives the
/// cooler type), 30 = Cell, 31 = Moderator, 32 = Air. <see cref="Active"/> is a sentinel, not a block.
/// </summary>
public static class ClassicTiles
{
    public const int Water = 0;
    public const int Redstone = 1;
    public const int Quartz = 2;
    public const int Gold = 3;
    public const int Glowstone = 4;
    public const int Lapis = 5;
    public const int Diamond = 6;
    public const int Helium = 7;
    public const int Enderium = 8;
    public const int Cryotheum = 9;
    public const int Iron = 10;
    public const int Emerald = 11;
    public const int Copper = 12;
    public const int Tin = 13;
    public const int Magnesium = 14;
    /// <summary>Sentinel: number of cooler types. Active cooler IDs are <c>Active + type</c>.</summary>
    public const int Active = 15;
    public const int Cell = Active * 2;
    public const int Moderator = Cell + 1;
    public const int Air = Moderator + 1;

    /// <summary>Number of placeable tile IDs (everything below Air). Size of <c>Settings.limit</c>.</summary>
    public const int NumPlaceable = Air;
    /// <summary>Number of cooler tile IDs (passive + active). Size of <c>Settings.coolingRates</c>.</summary>
    public const int NumCoolerIds = Cell;

    public const int NeutronReach = 4;
    public const double ModPower = 1.0;
    public const double ModHeat = 2.0;
}

public enum ClassicGoal
{
    Power = 0,
    Breeder = 1,
    Efficiency = 2,
}
