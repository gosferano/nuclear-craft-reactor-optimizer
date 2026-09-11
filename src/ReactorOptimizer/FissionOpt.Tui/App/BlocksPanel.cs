using System.Globalization;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tui.App;

/// <summary>The "Blocks" tab: cooling rates and per-block limits, with the three modpack rate presets.</summary>
public sealed class BlocksPanel : View
{
    private readonly TextField[] _rate = new TextField[Active];
    private readonly TextField[] _limit = new TextField[Active];
    private readonly TextField[] _activeRate = new TextField[Active];
    private readonly TextField[] _activeLimit = new TextField[Active];
    private readonly TextField _cellLimit, _modLimit;
    private readonly List<View> _inputs = new();

    public BlocksPanel()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();

        Add(new Label { X = 1, Y = 0, Text = "Leave a limit blank for unlimited; 0 disables the block. Active coolers are disabled by default." });
        const int c0 = 1, c1 = 22, c2 = 34, c3 = 44, c4 = 58;
        Add(new Label { X = c1, Y = 2, Text = "Rate (H/t)" });
        Add(new Label { X = c2, Y = 2, Text = "Max" });
        Add(new Label { X = c3, Y = 2, Text = "Active rate" });
        Add(new Label { X = c4, Y = 2, Text = "Max active" });
        var defaults = ClassicPresets.FindCoolingRates("Default")!.Rates;
        for (int t = 0; t < Active; ++t)
        {
            int y = 3 + t;
            Add(new Label { X = c0, Y = y, Text = $"{ClassicPresets.TileNames[t]}  {ClassicPresets.TileTitles[t]}" });
            _rate[t] = Field(c1, y, 9, Num(defaults[t]));
            _limit[t] = Field(c2, y, 7, "");
            _activeRate[t] = Field(c3, y, 9, Num(defaults[t + Active]));
            _activeLimit[t] = Field(c4, y, 7, "0");
            Add(_rate[t], _limit[t], _activeRate[t], _activeLimit[t]);
        }
        int cy = 3 + Active;
        Add(new Label { X = c0, Y = cy, Text = "[]  Reactor Cell" });
        _cellLimit = Field(c2, cy, 7, "");
        Add(new Label { X = c0, Y = cy + 1, Text = "##  Moderator" });
        _modLimit = Field(c2, cy + 1, 7, "");
        Add(_cellLimit, _modLimit);

        var presetLabel = new Label { X = c0, Y = cy + 3, Text = "Load cooling rates:" };
        Add(presetLabel);
        View prev = presetLabel;
        foreach (var p in ClassicPresets.CoolingRates)
        {
            var b = new Button { X = Pos.Right(prev) + 1, Y = cy + 3, Text = p.Config };
            var rates = p.Rates;
            b.Accepted += (_, _) => LoadRates(rates);
            Add(b);
            prev = b;
        }
    }

    private TextField Field(int x, int y, int width, string text)
    {
        var f = new TextField { X = x, Y = y, Width = width, Text = text };
        _inputs.Add(f);
        return f;
    }

    private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private void LoadRates(double[] rates)
    {
        for (int t = 0; t < Active; ++t)
        {
            _rate[t].Text = Num(rates[t]);
            _activeRate[t].Text = Num(rates[t + Active]);
        }
    }

    public void ApplyTo(ClassicSettings s)
    {
        for (int t = 0; t < Active; ++t)
        {
            s.CoolingRates[t] = Rate(_rate[t].Text, ClassicPresets.TileTitles[t]);
            s.CoolingRates[t + Active] = Rate(_activeRate[t].Text, "Active " + ClassicPresets.TileTitles[t]);
            s.Limit[t] = Limit(_limit[t].Text);
            s.Limit[t + Active] = Limit(_activeLimit[t].Text);
        }
        s.Limit[Cell] = Limit(_cellLimit.Text);
        s.Limit[Moderator] = Limit(_modLimit.Text);
    }

    private static double Rate(string text, string name)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !(v > 0))
            throw new ArgumentException($"Cooling rate for {name} must be a positive number");
        return v;
    }

    /// <summary>Blank or negative means unlimited (-1), like the web UI.</summary>
    private static int Limit(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : -1;

    public void SetEnabledAll(bool enabled)
    {
        foreach (var v in _inputs) v.Enabled = enabled;
    }
}
