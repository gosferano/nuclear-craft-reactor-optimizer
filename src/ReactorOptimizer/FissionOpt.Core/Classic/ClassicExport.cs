using System.Globalization;
using System.Text;
using System.Text.Json;
using FissionOpt.Core.Presets;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Core.Classic;

/// <summary>Text and JSON renderings of a classic design, mirroring <c>displaySample</c> in web/main.js.</summary>
public static class ClassicExport
{
    /// <summary>Two-character label for a tile ID (active coolers use the same label as their passive type).</summary>
    public static string Label(int tile) => ClassicPresets.TileNames[BaseType(tile)];

    public static string Title(int tile) =>
        IsActiveCooler(tile) ? "Active " + ClassicPresets.TileTitles[tile - Active] : ClassicPresets.TileTitles[BaseType(tile)];

    /// <summary>Name used in Hellrage's Reactor Planner JSON, or null for Air.</summary>
    public static string? SaveName(int tile)
    {
        if (tile == Air) return null;
        return IsActiveCooler(tile) ? "Active " + ClassicPresets.TileSaveNames[tile - Active] : ClassicPresets.TileSaveNames[BaseType(tile)];
    }

    public static bool IsActiveCooler(int tile) => tile >= Active && tile < Cell;

    /// <summary>Index into the 18-entry name tables: cooler type 0–14, 15 Cell, 16 Moderator, 17 Air.</summary>
    public static int BaseType(int tile)
    {
        if (tile < Active) return tile;
        if (tile < Cell) return tile - Active;
        return tile - Active; // Cell 30 -> 15, Moderator 31 -> 16, Air 32 -> 17
    }

    /// <summary>Number of casing blocks for the interior size, as the web UI reports it.</summary>
    public static int CasingCount(int sx, int sy, int sz) => (sx * sy + sy * sz + sz * sx) * 2;

    /// <summary>Counts per tile ID (Air included), for the block summary.</summary>
    public static int[] CountTiles(Grid3 state)
    {
        var counts = new int[Air + 1];
        foreach (var t in state.Data) ++counts[t];
        return counts;
    }

    /// <summary>Layer-by-layer text dump: one "Layer n" per x, rows over y, columns over z, active coolers marked with *.</summary>
    public static string RenderLayers(Grid3 state)
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
                    int t = state[x, y, z];
                    sb.Append(Label(t)).Append(IsActiveCooler(t) ? '*' : ' ');
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    public static string RenderMetrics(ClassicEvaluation v)
    {
        var sb = new StringBuilder();
        void Row(string label, double value, string unit) =>
            sb.Append(label.PadRight(14)).Append((Math.Round(value * 100) / 100).ToString(CultureInfo.InvariantCulture)).Append(' ').Append(unit).Append('\n');
        Row("Max Power", v.Power, "RF/t");
        Row("Heat", v.Heat, "H/t");
        Row("Cooling", v.Cooling, "H/t");
        Row("Net Heat", v.NetHeat, "H/t");
        Row("Duty Cycle", v.DutyCycle * 100, "%");
        Row("Fuel Use Rate", v.AvgBreed, "x");
        Row("Efficiency", v.Efficiency * 100, "%");
        Row("Avg Power", v.AvgPower, "RF/t");
        return sb.ToString();
    }

    public static string RenderBlockCounts(Grid3 state)
    {
        var counts = CountTiles(state);
        var rows = new List<(string name, int n)> { ("Casing", CasingCount(state.SizeX, state.SizeY, state.SizeZ)) };
        for (int t = 0; t < Air; ++t)
            if (counts[t] > 0)
                rows.Add((Title(t), counts[t]));
        rows.Sort((a, b) => b.n.CompareTo(a.n));
        var sb = new StringBuilder("Total number of blocks used\n");
        foreach (var (name, n) in rows)
            sb.Append("  ").Append(name.PadRight(18)).Append("x ").Append(n).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Hellrage Reactor Planner v1.2.24 JSON. Planner axes: X = internal z, Y = internal x (layers), Z = internal y,
    /// all 1-based. Air is omitted.
    /// </summary>
    public static string ToHellrageJson(ClassicSettings settings, Grid3 state, string fuelName = "")
    {
        var compressed = new Dictionary<string, List<Dictionary<string, int>>>();
        for (int x = 0; x < state.SizeX; ++x)
        for (int y = 0; y < state.SizeY; ++y)
        for (int z = 0; z < state.SizeZ; ++z)
        {
            string? name = SaveName(state[x, y, z]);
            if (name == null) continue;
            if (!compressed.TryGetValue(name, out var list))
                compressed[name] = list = new();
            list.Add(new Dictionary<string, int> { ["X"] = z + 1, ["Y"] = x + 1, ["Z"] = y + 1 });
        }
        var doc = new Dictionary<string, object>
        {
            ["UsedFuel"] = new Dictionary<string, object>
            {
                ["name"] = fuelName, ["FuelTime"] = 0.0,
                ["BasePower"] = settings.FuelBasePower, ["BaseHeat"] = settings.FuelBaseHeat,
            },
            ["SaveVersion"] = new Dictionary<string, int>
            {
                ["Major"] = 1, ["Minor"] = 2, ["Build"] = 24, ["Revision"] = 0, ["MajorRevision"] = 0, ["MinorRevision"] = 0,
            },
            ["InteriorDimensions"] = new Dictionary<string, int> { ["X"] = state.SizeZ, ["Y"] = state.SizeX, ["Z"] = state.SizeY },
            ["CompressedReactor"] = compressed,
        };
        return JsonSerializer.Serialize(doc);
    }
}
