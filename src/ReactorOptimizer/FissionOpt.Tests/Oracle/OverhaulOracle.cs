using System.Text;
using FissionOpt.Core;
using FissionOpt.Core.Overhaul;
using static FissionOpt.Tests.Oracle.OracleFormat;

namespace FissionOpt.Tests.Oracle;

public sealed class OracleEdge
{
    public bool Has;
    public double Efficiency;
    public int Flux, NModerators;
    public bool IsReflected;
}

public sealed class OracleTile
{
    public char Kind; // A C M R S I K H
    public int NeutronSource, Flux, HeatMult, Cluster;
    public bool Blocked, Excluded, Active, Functional;
    public double PositionalEfficiency, FluxEfficiency, Efficiency;
    public OracleEdge[] Edges = new OracleEdge[6];
}

public sealed class OracleCluster
{
    public double RawOutput, CoolingPenaltyMult, Output, RawEfficiency, Efficiency;
    public int Heat, Cooling, NetHeat;
    public bool HasCasingConnection;
    public List<Coord> Tiles = new();
}

public sealed class OverhaulOracleResult
{
    public List<(int fuel, int source)> CellTypes = new();
    public double MaxOutput;
    public int MinCriticality, MinHeat;
    public double RawEfficiency, Efficiency, RawOutput, Output, Density, SparsityPenalty;
    public int NFunctionalBlocks, TotalPositiveNetHeat, IrradiatorFlux, NActiveCells, TotalRawFlux, MaxCellFlux;
    public List<Coord> FluxRoots = new();
    public List<OracleCluster> Clusters = new();
    public OracleTile[] Tiles = Array.Empty<OracleTile>();
    public int[] Canonical = Array.Empty<int>();
}

/// <summary>Overhaul-mode request/response on top of <see cref="OracleProcess"/>.</summary>
public static class OverhaulOracle
{
    public static string FormatRequest(OverhaulSettings s, bool shieldOn, Grid3 state)
    {
        var sb = new StringBuilder();
        sb.Append("overhaul\n");
        Append(sb, s.SizeX); Append(sb, s.SizeY); Append(sb, s.SizeZ); sb.Append('\n');
        Append(sb, s.Fuels.Count); sb.Append('\n');
        foreach (var f in s.Fuels)
        {
            Append(sb, f.Efficiency); Append(sb, f.Limit); Append(sb, f.Criticality); Append(sb, f.Heat); Append(sb, f.SelfPriming);
            sb.Append('\n');
        }
        foreach (var l in s.Limits) Append(sb, l);
        sb.Append('\n');
        foreach (var l in s.SourceLimits) Append(sb, l);
        sb.Append('\n');
        Append(sb, (int)s.Goal); Append(sb, s.Controllable); Append(sb, s.SymX); Append(sb, s.SymY); Append(sb, s.SymZ); sb.Append('\n');
        Append(sb, shieldOn); sb.Append('\n');
        foreach (var t in state.Data) Append(sb, t);
        sb.Append('\n');
        return sb.ToString();
    }

    public static OverhaulOracleResult Evaluate(OracleProcess oracle, OverhaulSettings s, bool shieldOn, Grid3 state)
    {
        var lines = oracle.Exchange(FormatRequest(s, shieldOn, state));
        var r = new OverhaulOracleResult();
        int li = 0;
        var a = Tokens(lines[li++]);
        int p = 0;
        int nCellTypes = ParseInt(a[p++]);
        for (int i = 0; i < nCellTypes; ++i) r.CellTypes.Add((ParseInt(a[p++]), ParseInt(a[p++])));
        r.MaxOutput = ParseDouble(a[p++]); r.MinCriticality = ParseInt(a[p++]); r.MinHeat = ParseInt(a[p++]);

        a = Tokens(lines[li++]);
        r.RawEfficiency = ParseDouble(a[0]); r.Efficiency = ParseDouble(a[1]); r.RawOutput = ParseDouble(a[2]); r.Output = ParseDouble(a[3]);
        r.Density = ParseDouble(a[4]); r.SparsityPenalty = ParseDouble(a[5]);
        r.NFunctionalBlocks = ParseInt(a[6]); r.TotalPositiveNetHeat = ParseInt(a[7]); r.IrradiatorFlux = ParseInt(a[8]);
        r.NActiveCells = ParseInt(a[9]); r.TotalRawFlux = ParseInt(a[10]); r.MaxCellFlux = ParseInt(a[11]);

        a = Tokens(lines[li++]);
        int nRoots = ParseInt(a[0]);
        for (int i = 0; i < nRoots; ++i) r.FluxRoots.Add(new Coord(ParseInt(a[1 + 3 * i]), ParseInt(a[2 + 3 * i]), ParseInt(a[3 + 3 * i])));

        int nClusters = ParseInt(Tokens(lines[li++])[0]);
        for (int k = 0; k < nClusters; ++k)
        {
            a = Tokens(lines[li++]);
            var c = new OracleCluster
            {
                RawOutput = ParseDouble(a[0]), CoolingPenaltyMult = ParseDouble(a[1]), Output = ParseDouble(a[2]),
                RawEfficiency = ParseDouble(a[3]), Efficiency = ParseDouble(a[4]),
                Heat = ParseInt(a[5]), Cooling = ParseInt(a[6]), NetHeat = ParseInt(a[7]), HasCasingConnection = a[8] == "1",
            };
            int nTiles = ParseInt(a[9]);
            for (int i = 0; i < nTiles; ++i) c.Tiles.Add(new Coord(ParseInt(a[10 + 3 * i]), ParseInt(a[11 + 3 * i]), ParseInt(a[12 + 3 * i])));
            r.Clusters.Add(c);
        }

        int n = state.Length;
        r.Tiles = new OracleTile[n];
        for (int i = 0; i < n; ++i)
        {
            a = Tokens(lines[li++]);
            var t = new OracleTile { Kind = a[0][0] };
            switch (t.Kind)
            {
                case 'C':
                    t.NeutronSource = ParseInt(a[1]); t.Blocked = a[2] == "1"; t.Excluded = a[3] == "1"; t.Active = a[4] == "1";
                    t.Flux = ParseInt(a[5]); t.HeatMult = ParseInt(a[6]); t.Cluster = ParseInt(a[7]); t.PositionalEfficiency = ParseDouble(a[8]);
                    p = 9;
                    if (t.Cluster >= 0) { t.FluxEfficiency = ParseDouble(a[p++]); t.Efficiency = ParseDouble(a[p++]); }
                    for (int d = 0; d < 6; ++d)
                    {
                        var e = new OracleEdge();
                        if (a[p++] == "1")
                        {
                            e.Has = true;
                            e.Efficiency = ParseDouble(a[p++]); e.Flux = ParseInt(a[p++]); e.NModerators = ParseInt(a[p++]); e.IsReflected = a[p++] == "1";
                        }
                        t.Edges[d] = e;
                    }
                    break;
                case 'M': t.Active = a[1] == "1"; t.Functional = a[2] == "1"; break;
                case 'R': t.Active = a[1] == "1"; break;
                case 'S': case 'I': t.Flux = ParseInt(a[1]); t.Cluster = ParseInt(a[2]); break;
                case 'K': t.Cluster = ParseInt(a[1]); break;
                case 'H': t.Active = a[1] == "1"; t.Cluster = ParseInt(a[2]); break;
            }
            r.Tiles[i] = t;
        }
        r.Canonical = Tokens(lines[li++]).Select(ParseInt).ToArray();
        if (li != lines.Count) throw new InvalidOperationException($"unexpected trailing oracle output ({lines.Count - li} lines)");
        return r;
    }
}
