using FissionOpt.Core;
using FissionOpt.Core.Classic;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace FissionOpt.Tui.App;

/// <summary>
/// Draws a classic design layer by layer as a grid of two-character labels, colored by tile type,
/// with active coolers in reverse video. Layers (internal x) are laid out left to right and wrap
/// to fit the viewport width; the content scrolls when it does not fit.
/// </summary>
public sealed class ReactorView : View
{
    private Grid3? _state;
    private const int CellW = 3; // "Wt "

    public ReactorView()
    {
        CanFocus = true;
        ViewportSettings = ViewportSettingsFlags.HasScrollBars;
    }

    public void SetState(Grid3? state)
    {
        _state = state;
        UpdateContentSize();
        SetNeedsDraw();
    }

    private (int layerW, int layerH, int perRow) Layout()
    {
        if (_state == null) return (0, 0, 1);
        int layerW = _state.SizeZ * CellW + 1;
        int layerH = _state.SizeY + 2; // title line + rows + blank
        int perRow = Math.Max(1, Math.Max(1, Viewport.Width) / layerW);
        return (layerW, layerH, perRow);
    }

    private void UpdateContentSize()
    {
        if (_state == null) { SetContentSize(new System.Drawing.Size(0, 0)); return; }
        var (layerW, layerH, perRow) = Layout();
        int rows = (_state.SizeX + perRow - 1) / perRow;
        SetContentSize(new System.Drawing.Size(Math.Min(perRow, _state.SizeX) * layerW, rows * layerH));
    }

    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        UpdateContentSize();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_state == null) return true;
        var normal = GetAttributeForRole(VisualRole.Normal);
        var (layerW, layerH, perRow) = Layout();
        var vp = Viewport;
        for (int x = 0; x < _state.SizeX; ++x)
        {
            int ox = (x % perRow) * layerW - vp.X;
            int oy = (x / perRow) * layerH - vp.Y;
            if (oy + layerH < 0 || oy >= vp.Height || ox + layerW < 0 || ox >= vp.Width) continue;
            SetAttribute(normal);
            Put(ox, oy, $"Layer {x + 1}");
            for (int y = 0; y < _state.SizeY; ++y)
            {
                int row = oy + 1 + y;
                if (row < 0 || row >= vp.Height) continue;
                for (int z = 0; z < _state.SizeZ; ++z)
                {
                    int col = ox + z * CellW;
                    if (col < 0 || col >= vp.Width) continue;
                    int tile = _state[x, y, z];
                    SetAttribute(TileStyle.For(tile, normal.Background));
                    Move(col, row);
                    AddStr(ClassicExport.Label(tile));
                }
            }
        }
        return true;
    }

    private void Put(int col, int row, string s)
    {
        if (row < 0 || row >= Viewport.Height || col >= Viewport.Width) return;
        Move(Math.Max(col, 0), row);
        AddStr(col < 0 ? s.Substring(Math.Min(-col, s.Length)) : s);
    }
}
