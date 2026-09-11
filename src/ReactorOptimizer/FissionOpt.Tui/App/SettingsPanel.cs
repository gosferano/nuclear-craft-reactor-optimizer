using System.Collections.ObjectModel;
using System.Globalization;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Presets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using static FissionOpt.Core.Classic.ClassicTiles;

namespace FissionOpt.Tui.App;

/// <summary>
/// The "Settings" tab: size, fuel (searchable presets or manual), goal, symmetry and the toggles.
/// Axes are presented the way leu-235.com and Hellrage's planner do (X × Y × Z with Y vertical),
/// which map to internal (z, x, y) — see <see cref="ClassicExport.ToHellrageJson"/>.
/// </summary>
public sealed class SettingsPanel : View
{
    private readonly TextField _sizeX, _sizeY, _sizeZ, _power, _heat, _search, _seed;
    private readonly ListView _fuelList;
    private readonly ObservableCollection<string> _fuelItems = new();
    private readonly List<ClassicFuelPreset> _filtered = new();
    private readonly OptionSelector _goal;
    private readonly CheckBox _symX, _symY, _symZ, _accessible, _heatNeutral, _useNet, _incremental, _simdNet;
    private readonly Label _fuelLabel;
    private string _fuelName = "";

    public SettingsPanel()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true; // containers must be focusable for their fields to receive focus

        var sizeLabel = new Label { X = 1, Y = 1, Text = "Interior size X × Y × Z (Y is vertical):" };
        _sizeX = Field(Pos.Right(sizeLabel) + 1, 1, 4, "5");
        var x1 = new Label { X = Pos.Right(_sizeX) + 1, Y = 1, Text = "×" };
        _sizeY = Field(Pos.Right(x1) + 1, 1, 4, "5");
        var x2 = new Label { X = Pos.Right(_sizeY) + 1, Y = 1, Text = "×" };
        _sizeZ = Field(Pos.Right(x2) + 1, 1, 4, "5");
        Add(sizeLabel, _sizeX, x1, _sizeY, x2, _sizeZ);

        var fuelFrame = new FrameView { Title = "Fuel", X = 1, Y = 3, Width = 50, Height = 17 };
        var searchLabel = new Label { X = 0, Y = 0, Text = "Search:" };
        _search = new TextField { X = Pos.Right(searchLabel) + 1, Y = 0, Width = Dim.Fill(1), Text = "" };
        _fuelList = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(3) };
        _fuelList.SetSource(_fuelItems);
        _fuelLabel = new Label { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Text = "Fuel: (manual)" };
        var powerLabel = new Label { X = 0, Y = Pos.AnchorEnd(2), Text = "Base power" };
        _power = Field(Pos.Right(powerLabel) + 1, Pos.AnchorEnd(2), 9, "");
        var powerUnit = new Label { X = Pos.Right(_power) + 1, Y = Pos.AnchorEnd(2), Text = "RF/t" };
        var heatLabel = new Label { X = 0, Y = Pos.AnchorEnd(1), Text = "Base heat " };
        _heat = Field(Pos.Right(heatLabel) + 1, Pos.AnchorEnd(1), 9, "");
        var heatUnit = new Label { X = Pos.Right(_heat) + 1, Y = Pos.AnchorEnd(1), Text = "H/t" };
        fuelFrame.Add(searchLabel, _search, _fuelList, _fuelLabel, powerLabel, _power, powerUnit, heatLabel, _heat, heatUnit);
        Add(fuelFrame);

        var factorFrame = new FrameView { Title = "Third-party fuels (multiply power and heat)", X = 1, Y = Pos.Bottom(fuelFrame), Width = 50, Height = 5 };
        int fy = 0;
        foreach (var f in ClassicPresets.FuelFactors)
        {
            var b = new Button { X = 0, Y = fy++, Text = $"× {f.Factor:0.####}  {f.Description}", NoDecorations = true, ShadowStyle = ShadowStyles.None };
            double factor = f.Factor;
            b.Accepted += (_, _) => ApplyFactor(factor);
            factorFrame.Add(b);
        }
        Add(factorFrame);

        var optFrame = new FrameView { Title = "Options", X = Pos.Right(fuelFrame) + 1, Y = 3, Width = Dim.Fill(1), Height = 22 };
        var goalLabel = new Label { X = 0, Y = 0, Text = "Optimize for:" };
        _goal = new OptionSelector
        {
            X = 2, Y = 1,
            Labels = new[] { "Power", "Breeding (maximize fuel consumption rate)", "Efficiency" },
            Value = 0,
        };
        _accessible = Check(0, 5, "Ensure active coolers are accessible by piping", true);
        _heatNeutral = Check(0, 6, "Only generate heat-neutral reactors", true);
        var symLabel = new Label { X = 0, Y = 8, Text = "Mirror symmetry:" };
        _symX = Check(2, 9, "X", true);
        _symY = Check(2, 10, "Y", true);
        _symZ = Check(2, 11, "Z", true);
        _useNet = Check(0, 13, "Use reinforcement learning (value network)", true);
        _incremental = Check(0, 14, "Incremental evaluation (falls back to full evaluation if active coolers must be accessible)", true);
        _simdNet = Check(0, 15, "SIMD value net (faster; a seed reproduces a run only with the same setting)", true);
        var seedLabel = new Label { X = 0, Y = 17, Text = "Seed:" };
        _seed = Field(Pos.Right(seedLabel) + 1, 17, 12, "0");
        optFrame.Add(goalLabel, _goal, _accessible, _heatNeutral, symLabel, _symX, _symY, _symZ, _useNet, _incremental, _simdNet, seedLabel, _seed);
        Add(optFrame);

        _search.TextChanged += (_, _) => RefreshFuelList();
        _fuelList.Accepted += (_, _) => SelectFuel();
        _fuelList.ValueChanged += (_, _) => SelectFuel();
        RefreshFuelList();
        SelectDefaultFuel();
    }

    private static TextField Field(Pos x, Pos y, int width, string text) => new() { X = x, Y = y, Width = width, Text = text };

    private static CheckBox Check(int x, int y, string text, bool on) =>
        new() { X = x, Y = y, Text = text, Value = on ? CheckState.Checked : CheckState.UnChecked };

    private void RefreshFuelList()
    {
        string q = _search.Text.Trim();
        _filtered.Clear();
        _fuelItems.Clear();
        foreach (var f in ClassicPresets.Fuels)
        {
            if (q.Length > 0 && !f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _filtered.Add(f);
            _fuelItems.Add($"{f.DisplayName,-24}{f.BasePower,9:0.##}{f.BaseHeat,8:0.##}");
        }
    }

    private void SelectDefaultFuel()
    {
        int idx = _filtered.FindIndex(f => f.Id == "DefLEU235");
        if (idx >= 0) _fuelList.SelectedItem = idx;
        SelectFuel();
    }

    private void SelectFuel()
    {
        int? i = _fuelList.SelectedItem;
        if (i is null || i < 0 || i >= _filtered.Count) return;
        var f = _filtered[i.Value];
        _power.Text = f.BasePower.ToString(CultureInfo.InvariantCulture);
        _heat.Text = f.BaseHeat.ToString(CultureInfo.InvariantCulture);
        _fuelName = f.DisplayName;
        _fuelLabel.Text = "Fuel: " + f.DisplayName;
    }

    private void ApplyFactor(double factor)
    {
        if (!TryParse(_power.Text, out var p) || !TryParse(_heat.Text, out var h)) return;
        _power.Text = (p * factor).ToString("R", CultureInfo.InvariantCulture);
        _heat.Text = (h * factor).ToString("R", CultureInfo.InvariantCulture);
        _fuelName += $" ×{factor:0.####}";
        _fuelLabel.Text = "Fuel: " + _fuelName;
    }

    private static bool TryParse(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    public string FuelName => _fuelName;
    public bool UseNet => _useNet.Value == CheckState.Checked;
    /// <summary>Null = automatic; false = force the scalar evaluator.</summary>
    public bool? Incremental => _incremental.Value == CheckState.Checked ? null : false;
    public bool SimdNet => _simdNet.Value == CheckState.Checked;

    public int Seed => int.TryParse(_seed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
        ? s : throw new ArgumentException("Seed must be an integer");

    /// <summary>Fills size, fuel, goal and toggles. Throws <see cref="ArgumentException"/> with a user-facing message.</summary>
    public void ApplyTo(ClassicSettings s)
    {
        static int Size(string name, string text)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v <= 0)
                throw new ArgumentException($"Core size {name} must be a positive integer");
            return v;
        }
        static double Positive(string name, string text)
        {
            if (!TryParse(text, out var v) || !(v > 0))
                throw new ArgumentException($"{name} must be a positive number");
            return v;
        }
        // Planner/web axes -> internal: X -> z, Y -> x (layers), Z -> y.
        s.SizeZ = Size("X", _sizeX.Text);
        s.SizeX = Size("Y", _sizeY.Text);
        s.SizeY = Size("Z", _sizeZ.Text);
        s.FuelBasePower = Positive("Fuel base power", _power.Text);
        s.FuelBaseHeat = Positive("Fuel base heat", _heat.Text);
        s.Goal = (ClassicGoal)(_goal.Value ?? 0);
        s.EnsureActiveCoolerAccessible = _accessible.Value == CheckState.Checked;
        s.EnsureHeatNeutral = _heatNeutral.Value == CheckState.Checked;
        s.SymZ = _symX.Value == CheckState.Checked;
        s.SymX = _symY.Value == CheckState.Checked;
        s.SymY = _symZ.Value == CheckState.Checked;
    }

    public void SetEnabledAll(bool enabled)
    {
        foreach (var v in new View[] { _sizeX, _sizeY, _sizeZ, _power, _heat, _search, _seed, _fuelList, _goal, _symX, _symY, _symZ, _accessible, _heatNeutral, _useNet, _incremental, _simdNet })
            v.Enabled = enabled;
    }
}
