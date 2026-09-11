namespace FissionOpt.Core.Presets;

/// <param name="Type">Fuel form: "OX" (oxide), "NI" (nitride) or "ZA" (zirconium alloy).</param>
/// <param name="EfficiencyPercent">Efficiency as shown in the web table (percent); divide by 100 for <see cref="Overhaul.OverhaulFuel.Efficiency"/>.</param>
public sealed record OverhaulFuelPreset(string Type, string Fuel, double EfficiencyPercent, int Criticality, int Heat, bool SelfPriming)
{
    /// <summary>The name the web UI puts in the fuel row, e.g. "[OX]LEU-235" or "[OX]MOX-239" for MIX fuels.</summary>
    public string Name => "[" + Type + "]" + (Fuel.StartsWith("MIX", StringComparison.Ordinal) ? "M" + Type + Fuel.Substring(3) : Fuel);
    public string DisplayName => $"{Type} {Fuel}";
}

public static partial class OverhaulPresets
{
    public static OverhaulFuelPreset? Find(string type, string fuel)
    {
        foreach (var f in Fuels)
            if (string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Fuel, fuel, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }
}
