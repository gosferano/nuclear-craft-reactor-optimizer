using System.Globalization;
using System.Text;
using System.Text.Json;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Core.Overhaul;

/// <summary>Text and JSON renderings of an overhaul design, mirroring <c>displaySample</c> in web/overhaul.js.</summary>
public static class OverhaulExport
{
    private static readonly string[] SourceNames = { "None", "Cf-252", "Po-Be", "Ra-Be" };
    private static readonly char[] SourceSuffix = { '\0', 'A', 'B', 'C' };

    /// <summary>Two-character label. Cells are "1", "1S" (self-primed), "1A"/"1B"/"1C" (Cf-252 / Po-Be / Ra-Be), like the web UI.</summary>
    public static string Label(OverhaulSettings settings, int tile)
    {
        if (tile <= Air) return TileNamePadded(tile);
        var (fuel, source) = settings.CellTypes[tile - C0];
        string n = (fuel + 1).ToString(CultureInfo.InvariantCulture);
        if (settings.Fuels[fuel].SelfPriming) return n + "S";
        return source == 0 ? n + "." : n + SourceSuffix[source];
    }

    /// <summary>Single-character names ("B", "N") are padded with a dot, as the web UI does in the grid.</summary>
    private static string TileNamePadded(int tile)
    {
        string n = OverhaulPresets.TileNames[tile];
        return n.Length == 1 ? n + "." : n;
    }

    public static string Title(OverhaulSettings settings, int tile)
    {
        if (tile <= Air) return OverhaulPresets.TileTitles[tile];
        var (fuel, source) = settings.CellTypes[tile - C0];
        string t = $"Cell for Fuel #{fuel + 1}";
        if (settings.Fuels[fuel].SelfPriming) return t + ", Self-Primed";
        return source == 0 ? t : t + ", Primed by " + SourceNames[source];
    }

    public static int CasingCount(int sx, int sy, int sz) => (sx * sy + sy * sz + sz * sx) * 2 + (sx + sy + sz) * 4 + 8;

    public static string RenderLayers(OverhaulSettings settings, Grid3 state)
    {
        var sb = new StringBuilder();
        for (int x = 0; x < state.SizeX; ++x)
        {
            sb.Append("Layer ").Append(x + 1).Append('\n');
            for (int y = 0; y < state.SizeY; ++y)
            {
                for (int z = 0; z < state.SizeZ; ++z)
                {
                    if (z > 0) sb.Append(' ');
                    sb.Append(Label(settings, state[x, y, z]));
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    public static string RenderMetrics(OverhaulEvaluation v)
    {
        var sb = new StringBuilder();
        void Row(string label, double value, string unit) =>
            sb.Append(label.PadRight(12)).Append((Math.Round(value * 100) / 100).ToString(CultureInfo.InvariantCulture)).Append(' ').Append(unit).Append('\n');
        Row("Output", v.Output / 16, "mB/t");
        Row("Efficiency", v.Efficiency * 100, "%");
        Row("Fuel Use", v.NActiveCells, "x");
        Row("Irr. Flux", v.IrradiatorFlux, "N");
        return sb.ToString();
    }

    /// <summary>Block counts as (name, count), sorted descending like the web UI: casing, each block type, "Cell" and the neutron sources.</summary>
    public static List<(string name, int count)> BlockCounts(OverhaulSettings settings, Grid3 state)
    {
        var counts = new int[Air];
        int cells = 0;
        var sources = new int[4];
        foreach (var t in state.Data)
        {
            if (t < Air) ++counts[t];
            else if (t >= C0)
            {
                ++cells;
                int source = settings.CellTypes[t - C0].source;
                if (source != 0) ++sources[source];
            }
        }
        var rows = new List<(string, int)> { ("Casing", CasingCount(state.SizeX, state.SizeY, state.SizeZ)) };
        for (int t = 0; t < Air; ++t)
            if (counts[t] > 0)
                rows.Add((OverhaulPresets.TileTitles[t], counts[t]));
        if (cells > 0) rows.Add(("Cell", cells));
        for (int s = 1; s <= 3; ++s)
            if (sources[s] > 0)
                rows.Add((SourceNames[s], sources[s]));
        rows.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return rows;
    }

    public static string RenderBlockCounts(OverhaulSettings settings, Grid3 state)
    {
        var sb = new StringBuilder("Total number of blocks used\n");
        foreach (var (name, n) in BlockCounts(settings, state))
            sb.Append("  ").Append(name.PadRight(18)).Append("x ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>Overhaul planner JSON (SaveVersion 2.1.4), same shape as web/overhaul.js writes. Air is omitted.</summary>
    public static string ToPlannerJson(OverhaulSettings settings, Grid3 state)
    {
        var heatSinks = new Dictionary<string, List<Dictionary<string, int>>>();
        var moderators = new Dictionary<string, List<Dictionary<string, int>>>();
        var reflectors = new Dictionary<string, List<Dictionary<string, int>>>();
        var shields = new Dictionary<string, List<Dictionary<string, int>>>();
        var irradiators = new Dictionary<string, List<Dictionary<string, int>>>();
        var conductors = new List<Dictionary<string, int>>();
        var fuelCells = new Dictionary<string, List<Dictionary<string, int>>>();

        static void Push(Dictionary<string, List<Dictionary<string, int>>> cat, string name, Dictionary<string, int> pos)
        {
            if (!cat.TryGetValue(name, out var list)) cat[name] = list = new();
            list.Add(pos);
        }

        for (int x = 0; x < state.SizeX; ++x)
        for (int y = 0; y < state.SizeY; ++y)
        for (int z = 0; z < state.SizeZ; ++z)
        {
            int tile = state[x, y, z];
            if (tile == Air) continue;
            var pos = new Dictionary<string, int> { ["X"] = z + 1, ["Y"] = x + 1, ["Z"] = y + 1 };
            if (tile < M0) Push(heatSinks, OverhaulPresets.TileSaveNames[tile], pos);
            else if (tile < R0) Push(moderators, OverhaulPresets.TileSaveNames[tile], pos);
            else if (tile < Shield) Push(reflectors, OverhaulPresets.TileSaveNames[tile], pos);
            else if (tile == Shield) Push(shields, OverhaulPresets.TileSaveNames[tile], pos);
            else if (tile == Irradiator) Push(irradiators, OverhaulPresets.TileSaveNames[tile], pos);
            else if (tile == Conductor) conductors.Add(pos);
            else
            {
                var (fuel, source) = settings.CellTypes[tile - C0];
                var f = settings.Fuels[fuel];
                string name = f.SelfPriming ? f.Name + ";True;Self"
                    : source == 0 ? f.Name + ";False;None"
                    : f.Name + ";True;" + SourceNames[source];
                Push(fuelCells, name, pos);
            }
        }
        var doc = new Dictionary<string, object>
        {
            ["SaveVersion"] = new Dictionary<string, int>
            {
                ["Major"] = 2, ["Minor"] = 1, ["Build"] = 4, ["Revision"] = 0, ["MajorRevision"] = 0, ["MinorRevision"] = 0,
            },
            ["Data"] = new Dictionary<string, object>
            {
                ["InteriorDimensions"] = new Dictionary<string, int> { ["X"] = state.SizeZ, ["Y"] = state.SizeX, ["Z"] = state.SizeY },
                ["CoolantRecipeName"] = "Water to High Pressure Steam",
                ["HeatSinks"] = heatSinks,
                ["Moderators"] = moderators,
                ["Reflectors"] = reflectors,
                ["NeutronShields"] = shields,
                ["Irradiators"] = irradiators,
                ["Conductors"] = conductors,
                ["FuelCells"] = fuelCells,
            },
        };
        return JsonSerializer.Serialize(doc);
    }
}
