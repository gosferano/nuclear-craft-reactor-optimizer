using System.Globalization;
using FissionOpt.Core.Overhaul;
using FissionOpt.Core.Presets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using static FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Tui.App;

/// <summary>Overhaul block limits: one "max allowed" field per non-cell tile type.</summary>
public sealed class OverhaulBlocksPanel : View
{
    private readonly TextField[] _limits = new TextField[NumLimited];

    public OverhaulBlocksPanel()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true; // containers must be focusable for their fields to receive focus
        Add(new Label { X = 1, Y = 0, Text = "Max allowed per block type. Leave blank for unlimited; 0 disables the block." });
        const int rows = 20;
        for (int t = 0; t < NumLimited; ++t)
        {
            int col = t / rows, row = t % rows;
            int x = 1 + col * 40;
            Add(new Label { X = x, Y = 2 + row, Text = $"{OverhaulPresets.TileNames[t],-3} {OverhaulPresets.TileTitles[t]}" });
            _limits[t] = new TextField { X = x + 24, Y = 2 + row, Width = 7, Text = "" };
            Add(_limits[t]);
        }
    }

    public void ApplyTo(OverhaulSettings s)
    {
        for (int t = 0; t < NumLimited; ++t)
            s.Limits[t] = int.TryParse(_limits[t].Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : -1;
    }

    public void SetEnabledAll(bool enabled)
    {
        foreach (var f in _limits) f.Enabled = enabled;
    }
}
