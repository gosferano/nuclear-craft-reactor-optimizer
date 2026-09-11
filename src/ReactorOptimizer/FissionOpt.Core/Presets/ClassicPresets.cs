namespace FissionOpt.Core.Presets;

/// <param name="Config">Modpack config: "Default", "E2E" or "PO3".</param>
/// <param name="Fuel">Fuel name as shown in the leu-235.com table header, e.g. "LEU-235".</param>
/// <param name="Variant">"Normal", "Oxide", or "" when the fuel has a single entry.</param>
/// <param name="Id">The element id in web/index.html, kept for traceability.</param>
public sealed record ClassicFuelPreset(string Config, string Fuel, string Variant, double BasePower, double BaseHeat, string Id)
{
    public string DisplayName => Variant.Length == 0 ? $"{Config} {Fuel}" : $"{Config} {Fuel} {Variant}";
}

/// <param name="Rates">30 entries: cooling rate per cooler tile ID (0–14 passive, 15–29 active).</param>
public sealed record CoolingRatePreset(string Config, double[] Rates);

/// <summary>A multiplier applied to both base power and base heat for third-party fuels.</summary>
public sealed record FuelFactor(string Id, string Description, double Factor);

public static partial class ClassicPresets
{
    public static ClassicFuelPreset? FindFuel(string config, string fuel, string variant = "Normal")
    {
        foreach (var f in Fuels)
            if (Eq(f.Config, config) && Eq(f.Fuel, fuel) && (Eq(f.Variant, variant) || f.Variant.Length == 0))
                return f;
        return null;
    }

    public static CoolingRatePreset? FindCoolingRates(string config)
    {
        foreach (var r in CoolingRates)
            if (Eq(r.Config, config))
                return r;
        return null;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
