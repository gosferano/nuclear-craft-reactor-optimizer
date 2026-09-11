using FissionOpt.Core.Classic;
using FissionOpt.Core.Overhaul;
using FissionOpt.Core.Presets;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;
using OT = FissionOpt.Core.Overhaul.OverhaulTiles;

namespace FissionOpt.Tui.App;

/// <summary>Colors for the two-character tile labels, generated from web/main.css and web/overhaul.css so the TUI matches leu-235.com.</summary>
public static class TileStyle
{
    private static readonly Color CellBg = new(90, 90, 90);
    private static readonly Color ModBg = new(200, 200, 200);
    private static readonly Color Black = new(0, 0, 0);
    private static readonly Color White = new(255, 255, 255);

    private static Color Rgb(int rgb) => new((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    /// <summary>Pure blue on a dark terminal is nearly invisible; lift it a little.</summary>
    private static Color Readable(int rgb) => rgb == 0x0000FF ? new Color(64, 64, 255) : Rgb(rgb);

    public static Attribute Classic(int tile, Color background)
    {
        int b = ClassicExport.BaseType(tile);
        if (b == 15) return new Attribute(White, CellBg);                  // cell: web draws gray text on white
        if (b == 16) return new Attribute(Black, ModBg);                   // moderator: web draws black text
        if (b == 17) return new Attribute(new Color(96, 96, 96), background); // air
        var fg = Readable(ClassicPresets.TileColors[b]);
        // Active coolers are drawn in reverse video: black label on the cooler's color.
        return ClassicExport.IsActiveCooler(tile) ? new Attribute(Black, fg) : new Attribute(fg, background);
    }

    public static Attribute Overhaul(int tile, Color background)
    {
        if (tile >= OT.C0) return new Attribute(White, CellBg);
        if (tile == OT.Air) return new Attribute(new Color(96, 96, 96), background);
        if (tile == OT.M0) return new Attribute(Black, ModBg);             // graphite: web draws black text
        return new Attribute(Readable(OverhaulPresets.TileColors[tile]), background);
    }
}
