using System.Globalization;
using System.Text;

namespace FissionOpt.Tests.Oracle;

/// <summary>Token helpers for the oracle protocol. Doubles round-trip exactly via %.17g / "R".</summary>
internal static class OracleFormat
{
    public static void Append(StringBuilder sb, int v) => sb.Append(v.ToString(CultureInfo.InvariantCulture)).Append(' ');
    public static void Append(StringBuilder sb, bool v) => sb.Append(v ? '1' : '0').Append(' ');

    public static void Append(StringBuilder sb, double v)
    {
        // The oracle reads with operator>>, which accepts "inf"/"nan" only in some libstdc++
        // versions, so refuse to send non-finite settings.
        if (!double.IsFinite(v)) throw new ArgumentException("non-finite settings value: " + v);
        sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
    }

    public static double ParseDouble(string tok) => tok switch
    {
        "NaN" => double.NaN,
        "Infinity" => double.PositiveInfinity,
        "-Infinity" => double.NegativeInfinity,
        _ => double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    public static int ParseInt(string tok) => int.Parse(tok, CultureInfo.InvariantCulture);

    public static string[] Tokens(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
