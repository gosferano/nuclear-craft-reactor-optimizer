using System.Collections.ObjectModel;
using System.Globalization;
using FissionOpt.Core.Overhaul;
using FissionOpt.Core.Presets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace FissionOpt.Tui.App;

/// <summary>Overhaul settings: size, fuel table (from presets or manual), neutron-source limits, goal and toggles.</summary>
public sealed class OverhaulSettingsPanel : View
{
    private readonly TextField _sizeX, _sizeY, _sizeZ, _search, _fuelLimit, _mName, _mEff, _mHeat, _mCrit, _seed;
    private readonly TextField[] _sourceLimits = new TextField[3];
    private readonly CheckBox _mSelf, _controllable, _symX, _symY, _symZ, _simdNet;
    private readonly ListView _presetList, _fuelList;
    private readonly ObservableCollection<string> _presetItems = new(), _fuelItems = new();
    private readonly List<OverhaulFuelPreset> _filtered = new();
    private readonly List<OverhaulFuel> _fuels = new();
    private readonly OptionSelector _goal;

    public OverhaulSettingsPanel()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true; // containers must be focusable for their fields to receive focus

        var sizeLabel = new Label { X = 1, Y = 0, Text = "Interior size X × Y × Z (Y is vertical):" };
        _sizeX = Field(Pos.Right(sizeLabel) + 1, 0, 4, "7");
        var x1 = new Label { X = Pos.Right(_sizeX) + 1, Y = 0, Text = "×" };
        _sizeY = Field(Pos.Right(x1) + 1, 0, 4, "7");
        var x2 = new Label { X = Pos.Right(_sizeY) + 1, Y = 0, Text = "×" };
        _sizeZ = Field(Pos.Right(x2) + 1, 0, 4, "7");
        Add(sizeLabel, _sizeX, x1, _sizeY, x2, _sizeZ);

        var presetFrame = new FrameView { Title = "Fuel presets", X = 1, Y = 2, Width = 44, Height = 14 };
        var searchLabel = new Label { X = 0, Y = 0, Text = "Search:" };
        _search = new TextField { X = Pos.Right(searchLabel) + 1, Y = 0, Width = Dim.Fill(1), Text = "" };
        _presetList = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        _presetList.SetSource(_presetItems);
        var limitLabel = new Label { X = 0, Y = Pos.AnchorEnd(1), Text = "Max cells:" };
        _fuelLimit = Field(Pos.Right(limitLabel) + 1, Pos.AnchorEnd(1), 6, "");
        var addPreset = new Button { X = Pos.Right(_fuelLimit) + 1, Y = Pos.AnchorEnd(1), Text = "Add preset", NoDecorations = true, ShadowStyle = ShadowStyles.None };
        addPreset.Accepted += (_, _) => AddPreset();
        _presetList.Accepted += (_, _) => AddPreset();
        presetFrame.Add(searchLabel, _search, _presetList, limitLabel, _fuelLimit, addPreset);
        Add(presetFrame);

        var manualFrame = new FrameView { Title = "Manual fuel", X = 1, Y = Pos.Bottom(presetFrame), Width = 44, Height = 6 };
        var nameLabel = new Label { X = 0, Y = 0, Text = "Name" };
        _mName = Field(6, 0, Dim.Fill(1), "");
        var effLabel = new Label { X = 0, Y = 1, Text = "Eff %" };
        _mEff = Field(6, 1, 7, "");
        var heatLabel = new Label { X = 14, Y = 1, Text = "Heat" };
        _mHeat = Field(19, 1, 7, "");
        var critLabel = new Label { X = 27, Y = 1, Text = "Crit" };
        _mCrit = Field(32, 1, 7, "");
        _mSelf = new CheckBox { X = 0, Y = 2, Text = "Self-priming", Value = CheckState.UnChecked };
        var addManual = new Button { X = 20, Y = 2, Text = "Add manual", NoDecorations = true, ShadowStyle = ShadowStyles.None };
        addManual.Accepted += (_, _) => AddManual();
        manualFrame.Add(nameLabel, _mName, effLabel, _mEff, heatLabel, _mHeat, critLabel, _mCrit, _mSelf, addManual);
        Add(manualFrame);

        var fuelFrame = new FrameView { Title = "Fuels in this reactor", X = Pos.Right(presetFrame) + 1, Y = 2, Width = Dim.Fill(1), Height = 9 };
        _fuelList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        _fuelList.SetSource(_fuelItems);
        var remove = new Button { X = 0, Y = Pos.AnchorEnd(1), Text = "Remove selected", NoDecorations = true, ShadowStyle = ShadowStyles.None };
        remove.Accepted += (_, _) => RemoveSelected();
        fuelFrame.Add(_fuelList, remove);
        Add(fuelFrame);

        var optFrame = new FrameView { Title = "Options", X = Pos.Right(presetFrame) + 1, Y = Pos.Bottom(fuelFrame), Width = Dim.Fill(1), Height = 18 };
        var srcLabel = new Label { X = 0, Y = 0, Text = "Max neutron sources:  Cf-252" };
        _sourceLimits[0] = Field(Pos.Right(srcLabel) + 1, 0, 5, "");
        var poLabel = new Label { X = Pos.Right(_sourceLimits[0]) + 1, Y = 0, Text = "Po-Be" };
        _sourceLimits[1] = Field(Pos.Right(poLabel) + 1, 0, 5, "0");
        var raLabel = new Label { X = Pos.Right(_sourceLimits[1]) + 1, Y = 0, Text = "Ra-Be" };
        _sourceLimits[2] = Field(Pos.Right(raLabel) + 1, 0, 5, "0");
        var goalLabel = new Label { X = 0, Y = 2, Text = "Optimize for:" };
        _goal = new OptionSelector
        {
            X = 2, Y = 3,
            Labels = new[] { "Output", "Breeding (maximize fuel consumption rate)", "Efficiency", "Irradiation" },
            Value = 0,
        };
        _controllable = new CheckBox { X = 0, Y = 8, Text = "Only generate reactors that can be turned off (shields)", Value = CheckState.UnChecked };
        var symLabel = new Label { X = 0, Y = 10, Text = "Mirror symmetry:" };
        _symX = new CheckBox { X = Pos.Right(symLabel) + 1, Y = 10, Text = "X", Value = CheckState.Checked };
        _symY = new CheckBox { X = Pos.Right(_symX) + 2, Y = 10, Text = "Y", Value = CheckState.Checked };
        _symZ = new CheckBox { X = Pos.Right(_symY) + 2, Y = 10, Text = "Z", Value = CheckState.Checked };
        _simdNet = new CheckBox { X = 0, Y = 12, Text = "SIMD value net (faster; a seed reproduces a run only with the same setting)", Value = CheckState.Checked };
        var seedLabel = new Label { X = 0, Y = 14, Text = "Seed:" };
        _seed = Field(Pos.Right(seedLabel) + 1, 14, 12, "0");
        optFrame.Add(srcLabel, _sourceLimits[0], poLabel, _sourceLimits[1], raLabel, _sourceLimits[2], goalLabel, _goal, _controllable, symLabel, _symX, _symY, _symZ, _simdNet, seedLabel, _seed);
        Add(optFrame);

        _search.TextChanged += (_, _) => RefreshPresets();
        RefreshPresets();
        int idx = _filtered.FindIndex(f => f.Type == "OX" && f.Fuel == "LEU-235");
        if (idx >= 0) { _presetList.SelectedItem = idx; AddPreset(); }
    }

    private static TextField Field(Pos x, Pos y, Dim width, string text) => new() { X = x, Y = y, Width = width, Text = text };

    private void RefreshPresets()
    {
        string q = _search.Text.Trim();
        _filtered.Clear();
        _presetItems.Clear();
        foreach (var f in OverhaulPresets.Fuels)
        {
            if (q.Length > 0 && !f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _filtered.Add(f);
            _presetItems.Add($"{f.DisplayName,-13}{f.EfficiencyPercent,5:0}% {f.Heat,5}H {f.Criticality,4}N{(f.SelfPriming ? " self" : "")}");
        }
    }

    private int ParseLimit(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : -1;

    private void AddPreset()
    {
        int? i = _presetList.SelectedItem;
        if (i is null || i < 0 || i >= _filtered.Count) return;
        var p = _filtered[i.Value];
        AddFuel(new OverhaulFuel(p.EfficiencyPercent / 100, ParseLimit(_fuelLimit.Text), p.Criticality, p.Heat, p.SelfPriming, p.Name));
    }

    private void AddManual()
    {
        try
        {
            var f = new OverhaulFuel(
                Positive("Efficiency", _mEff.Text) / 100, ParseLimit(_fuelLimit.Text),
                PositiveInt("Criticality", _mCrit.Text), PositiveInt("Heat", _mHeat.Text),
                _mSelf.Value == CheckState.Checked, _mName.Text.Trim().Length > 0 ? _mName.Text.Trim() : "Custom");
            AddFuel(f);
        }
        catch (ArgumentException e)
        {
            MessageBox.ErrorQuery(App!, "Invalid fuel", e.Message, "OK");
        }
    }

    private void AddFuel(OverhaulFuel f)
    {
        _fuels.Add(f);
        _fuelItems.Add($"{_fuels.Count}  {f.Name,-16} {f.Efficiency * 100,4:0}% {f.Heat,5}H {f.Criticality,4}N{(f.SelfPriming ? " self" : "")}  max {(f.Limit < 0 ? "-" : f.Limit.ToString(CultureInfo.InvariantCulture))}");
    }

    private void RemoveSelected()
    {
        int? i = _fuelList.SelectedItem;
        if (i is null || i < 0 || i >= _fuels.Count) return;
        _fuels.RemoveAt(i.Value);
        var copy = _fuels.ToList();
        _fuels.Clear();
        _fuelItems.Clear();
        foreach (var f in copy) AddFuel(f);
    }

    private static double Positive(string name, string text)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !(v > 0))
            throw new ArgumentException($"{name} must be a positive number");
        return v;
    }

    private static int PositiveInt(string name, string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v <= 0)
            throw new ArgumentException($"{name} must be a positive integer");
        return v;
    }

    public int Seed => int.TryParse(_seed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
        ? s : throw new ArgumentException("Seed must be an integer");

    public bool SimdNet => _simdNet.Value == CheckState.Checked;

    public void ApplyTo(OverhaulSettings s)
    {
        s.SizeZ = PositiveInt("Core size X", _sizeX.Text);
        s.SizeX = PositiveInt("Core size Y", _sizeY.Text);
        s.SizeY = PositiveInt("Core size Z", _sizeZ.Text);
        if (_fuels.Count == 0) throw new ArgumentException("No fuel. Add at least one fuel.");
        s.Fuels.Clear();
        foreach (var f in _fuels)
            s.Fuels.Add(new OverhaulFuel(f.Efficiency, f.Limit, f.Criticality, f.Heat, f.SelfPriming, f.Name));
        for (int i = 0; i < 3; ++i) s.SourceLimits[i] = ParseLimit(_sourceLimits[i].Text);
        s.Goal = (OverhaulGoal)(_goal.Value ?? 0);
        s.Controllable = _controllable.Value == CheckState.Checked;
        s.SymZ = _symX.Value == CheckState.Checked;
        s.SymX = _symY.Value == CheckState.Checked;
        s.SymY = _symZ.Value == CheckState.Checked;
    }

    public void SetEnabledAll(bool enabled)
    {
        foreach (var v in new View[] { _sizeX, _sizeY, _sizeZ, _search, _presetList, _fuelLimit, _mName, _mEff, _mHeat, _mCrit, _mSelf, _fuelList, _goal, _controllable, _symX, _symY, _symZ, _simdNet, _seed })
            v.Enabled = enabled;
        foreach (var v in _sourceLimits) v.Enabled = enabled;
    }
}
