using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Overhaul;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace FissionOpt.Tui.App;

/// <summary>Top-level window: mode switch, Settings / Blocks / Reactor tabs, a status bar with the controls, and the active session.</summary>
public sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly Tabs _tabs;
    private readonly View _settingsTab, _blocksTab;
    private readonly OptionSelector _mode;
    private readonly SettingsPanel _classicSettings;
    private readonly BlocksPanel _classicBlocks;
    private readonly OverhaulSettingsPanel _overhaulSettings;
    private readonly OverhaulBlocksPanel _overhaulBlocks;
    private readonly RunPanel _run;
    private readonly Shortcut _runKey, _pauseKey, _stopKey, _saveKey;
    private ISession? _session;
    private object? _timer;
    private readonly double[] _loss = new double[256];

    private bool OverhaulMode => _mode.Value == 1;

    public MainWindow(IApplication app)
    {
        _app = app;
        Title = "FissionOpt — NuclearCraft fission reactor optimizer";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Single;

        var modeLabel = new Label { X = 1, Y = 0, Text = "NuclearCraft version:" };
        _mode = new OptionSelector
        {
            X = Pos.Right(modeLabel) + 1, Y = 0,
            Orientation = Orientation.Horizontal,
            Labels = new[] { "_Classic (pre-overhaul)", "_Overhaul" },
            Value = 0,
        };
        _settingsTab = new View { Title = "_Settings", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _classicSettings = new SettingsPanel();
        _overhaulSettings = new OverhaulSettingsPanel { Visible = false };
        _settingsTab.Add(_classicSettings, _overhaulSettings);

        _blocksTab = new View { Title = "_Blocks", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _classicBlocks = new BlocksPanel();
        _overhaulBlocks = new OverhaulBlocksPanel { Visible = false };
        _blocksTab.Add(_classicBlocks, _overhaulBlocks);

        _run = new RunPanel();
        _tabs = new Tabs { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        _tabs.Add(_settingsTab, _blocksTab, _run);

        _runKey = new Shortcut(Key.F5, "Run", Run, "");
        _pauseKey = new Shortcut(Key.F6, "Pause", TogglePause, "");
        _stopKey = new Shortcut(Key.F7, "Stop", Stop, "");
        _saveKey = new Shortcut(Key.F8, "Save JSON", Save, "");
        var quitKey = new Shortcut(Key.Q.WithCtrl, "Quit", Quit, "");
        var bar = new StatusBar(new[] { _runKey, _pauseKey, _stopKey, _saveKey, quitKey }) { Y = Pos.AnchorEnd(1) };
        Add(modeLabel, _mode, _tabs, bar);

        _mode.ValueChanged += (_, _) => ApplyMode();
        ApplyMode();
        UpdateControls();
    }

    private void ApplyMode()
    {
        bool overhaul = OverhaulMode;
        _classicSettings.Visible = !overhaul;
        _classicBlocks.Visible = !overhaul;
        _overhaulSettings.Visible = overhaul;
        _overhaulBlocks.Visible = overhaul;
        _settingsTab.SetNeedsDraw();
        _blocksTab.SetNeedsDraw();
    }

    private void UpdateControls()
    {
        bool running = _session != null && !_session.IsFinished;
        _runKey.Enabled = !running;
        _pauseKey.Enabled = running;
        _pauseKey.CommandView.Text = _session is { IsPaused: true } ? "Resume" : "Pause";
        _stopKey.Enabled = running;
        _saveKey.Enabled = _session is { HasDesign: true };
        _mode.Enabled = !running;
        _classicSettings.SetEnabledAll(!running);
        _classicBlocks.SetEnabledAll(!running);
        _overhaulSettings.SetEnabledAll(!running);
        _overhaulBlocks.SetEnabledAll(!running);
    }

    private void Run()
    {
        if (_session != null && !_session.IsFinished) return;
        ISession session;
        try
        {
            if (OverhaulMode)
            {
                var s = new OverhaulSettings();
                _overhaulSettings.ApplyTo(s);
                _overhaulBlocks.ApplyTo(s);
                session = new OverhaulSession(s, _overhaulSettings.Seed);
            }
            else
            {
                var s = new ClassicSettings();
                _classicSettings.ApplyTo(s);
                _classicBlocks.ApplyTo(s);
                session = new ClassicSession(s, _classicSettings.UseNet, _classicSettings.Seed, _classicSettings.FuelName, _classicSettings.Incremental);
            }
        }
        catch (ArgumentException e)
        {
            MessageBox.ErrorQuery(_app, "Invalid settings", e.Message, "OK");
            return;
        }
        DisposeSession();
        _session = session;
        _run.Clear();
        _session.ConfigurePanel(_run);
        _session.Start();
        _tabs.Value = _run;
        _run.ShowProgress($"Starting (seed {_session.Seed})…");
        _timer ??= _app.AddTimeout(TimeSpan.FromMilliseconds(100), Poll);
        UpdateControls();
    }

    private bool Poll()
    {
        if (_session == null) return true;
        if (_session.Error != null)
        {
            var err = _session.Error;
            DisposeSession();
            _run.ShowProgress("Optimizer crashed: " + err.Message);
            MessageBox.ErrorQuery(_app, "Optimizer error", err.ToString(), "OK");
            UpdateControls();
            return true;
        }
        bool hadDesign = _session.HasDesign;
        _session.RefreshBest(_run);
        if (_session.TryTakeLossHistory(_loss))
            _run.ShowLoss(_loss, _run.LossWidth);
        var p = _session.Progress;
        string state = p.Finished ? "Stopped" : p.Paused ? "Paused" : $"{p.StepsPerSecond:0} steps/s";
#if DEBUG
        state += "  [DEBUG BUILD: ~3.5× slower, run with -c Release]";
#endif
        _run.ShowProgress($"{_session.StageText(p)}  —  {state}  (seed {_session.Seed}, {p.Steps:N0} steps)");
        if (p.Finished || hadDesign != _session.HasDesign) UpdateControls();
        return true;
    }

    private void TogglePause()
    {
        if (_session == null || _session.IsFinished) return;
        if (_session.IsPaused) _session.Resume(); else _session.Pause();
        UpdateControls();
    }

    private void Stop()
    {
        if (_session == null) return;
        _session.Stop();
        Poll(); // publish the final best
        UpdateControls();
    }

    private void Save()
    {
        if (_session is not { HasDesign: true }) return;
        var dialog = new SaveDialog { Title = "Save reactor planner JSON" };
        dialog.Path = Path.Combine(Environment.CurrentDirectory, "reactor.json");
        _app.Run(dialog);
        if (dialog.Canceled || string.IsNullOrWhiteSpace(dialog.Path)) return;
        try
        {
            File.WriteAllText(dialog.Path, _session.ExportJson());
            _run.ShowProgress($"Saved {dialog.Path}");
        }
        catch (Exception e)
        {
            MessageBox.ErrorQuery(_app, "Save failed", e.Message, "OK");
        }
    }

    private void Quit()
    {
        DisposeSession();
        _app.RequestStop();
    }

    private void DisposeSession()
    {
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timer != null) _app.RemoveTimeout(_timer);
            DisposeSession();
        }
        base.Dispose(disposing);
    }
}
