using FissionOpt.Core.Classic;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace FissionOpt.Tui.App;

/// <summary>Colors for the two-character tile labels, taken from web/main.css so the TUI matches leu-235.com.</summary>
public static class TileStyle
{
    // Indexed like ClassicPresets.TileNames: 15 cooler types, Cell, Moderator, Air.
    private static readonly Color[] Foreground =
    {
        new(30, 144, 255),  // Wt dodgerblue
        new(255, 0, 0),     // Rs red
        new(211, 211, 211), // Qz lightgray
        new(255, 215, 0),   // Au gold
        new(221, 204, 0),   // Gs #dc0
        new(64, 64, 255),   // Lp blue (lightened a little for dark terminals)
        new(176, 224, 230), // Dm powderblue
        new(240, 128, 128), // He lightcoral
        new(0, 160, 160),   // Ed teal
        new(0, 191, 255),   // Cr deepskyblue
        new(245, 222, 179), // Fe wheat
        new(0, 200, 0),     // Em green
        new(190, 90, 60),   // Cu brown
        new(176, 196, 222), // Sn lightsteelblue
        new(255, 192, 203), // Mg pink
        new(255, 255, 255), // Cell: white on gray (web: gray text)
        new(0, 0, 0),       // Moderator: black on light (web: black text)
        new(96, 96, 96),    // Air
    };

    public static Attribute For(int tile, Color background)
    {
        int b = ClassicExport.BaseType(tile);
        if (b == 15) return new Attribute(Foreground[15], new Color(90, 90, 90));
        if (b == 16) return new Attribute(Foreground[16], new Color(200, 200, 200));
        // Active coolers are drawn in reverse video: black label on the cooler's color.
        if (ClassicExport.IsActiveCooler(tile)) return new Attribute(new Color(0, 0, 0), Foreground[b]);
        return new Attribute(Foreground[b], background);
    }
}
