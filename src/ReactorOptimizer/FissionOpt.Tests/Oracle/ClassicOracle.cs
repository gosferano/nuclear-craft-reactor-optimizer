using System.Text;
using FissionOpt.Core;
using FissionOpt.Core.Classic;
using static FissionOpt.Tests.Oracle.OracleFormat;

namespace FissionOpt.Tests.Oracle;

public sealed class ClassicOracleResult
{
    public double PowerMult, HeatMult, Cooling;
    public int Breed;
    public double Heat, NetHeat, DutyCycle, AvgMult, Power, AvgPower, AvgBreed, Efficiency;
    public List<Coord> InvalidTiles = new();
}

/// <summary>Classic-mode request/response on top of <see cref="OracleProcess"/>.</summary>
public static class ClassicOracle
{
    public static string FormatRequest(ClassicSettings s, Grid3 state)
    {
        var sb = new StringBuilder();
        sb.Append("classic\n");
        Append(sb, s.SizeX); Append(sb, s.SizeY); Append(sb, s.SizeZ); sb.Append('\n');
        Append(sb, s.FuelBasePower); Append(sb, s.FuelBaseHeat); sb.Append('\n');
        foreach (var l in s.Limit) Append(sb, l);
        sb.Append('\n');
        foreach (var r in s.CoolingRates) Append(sb, r);
        sb.Append('\n');
        Append(sb, s.EnsureActiveCoolerAccessible); Append(sb, s.EnsureHeatNeutral); Append(sb, (int)s.Goal);
        Append(sb, s.SymX); Append(sb, s.SymY); Append(sb, s.SymZ); sb.Append('\n');
        // Grid3 is x-major like the oracle expects, so the flat array is already in order.
        foreach (var t in state.Data) Append(sb, t);
        sb.Append('\n');
        return sb.ToString();
    }

    public static ClassicOracleResult Evaluate(OracleProcess oracle, ClassicSettings s, Grid3 state)
    {
        var lines = oracle.Exchange(FormatRequest(s, state));
        if (lines.Count != 2) throw new InvalidOperationException("unexpected oracle response: " + string.Join("|", lines));
        var a = Tokens(lines[0]);
        var r = new ClassicOracleResult
        {
            PowerMult = ParseDouble(a[0]),
            HeatMult = ParseDouble(a[1]),
            Cooling = ParseDouble(a[2]),
            Breed = ParseInt(a[3]),
            Heat = ParseDouble(a[4]),
            NetHeat = ParseDouble(a[5]),
            DutyCycle = ParseDouble(a[6]),
            AvgMult = ParseDouble(a[7]),
            Power = ParseDouble(a[8]),
            AvgPower = ParseDouble(a[9]),
            AvgBreed = ParseDouble(a[10]),
            Efficiency = ParseDouble(a[11]),
        };
        var b = Tokens(lines[1]);
        int n = ParseInt(b[0]);
        for (int i = 0; i < n; ++i)
            r.InvalidTiles.Add(new Coord(ParseInt(b[1 + 3 * i]), ParseInt(b[2 + 3 * i]), ParseInt(b[3 + 3 * i])));
        return r;
    }
}
