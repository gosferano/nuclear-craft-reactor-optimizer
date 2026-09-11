using FissionOpt.Core.Classic;
using FissionOpt.Tui.Headless;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace FissionOpt.Tui.App;

/// <summary>Top-level window: Settings / Blocks / Reactor tabs, a status bar with the controls, and the optimizer runner.</summary>
public sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly Tabs _tabs;
    private readonly SettingsPanel _settings;
    private readonly BlocksPanel _blocks;
    private readonly RunPanel _run;
    private readonly Shortcut _runKey, _pauseKey, _stopKey, _saveKey;
    private ClassicRunner? _runner;
    private ClassicSample? _shown;
    private ClassicSettings? _activeSettings;
    private string _fuelName = "";
    private object? _timer;
    private readonly double[] _loss = new double[ClassicOpt.NLossHistory];

    public MainWindow(IApplication app)
    {
        _app = app;
        Title = "FissionOpt — NuclearCraft fission reactor optimizer (classic)";
        BorderStyle = Terminal.Gui.Drawing.LineStyle.Single;

        _settings = new SettingsPanel();
        _blocks = new BlocksPanel();
        _run = new RunPanel();
        _tabs = new Tabs { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
        _tabs.Add(_settings, _blocks, _run);

        _runKey = new Shortcut(Key.F5, "Run", Run, "");
        _pauseKey = new Shortcut(Key.F6, "Pause", TogglePause, "");
        _stopKey = new Shortcut(Key.F7, "Stop", Stop, "");
        _saveKey = new Shortcut(Key.F8, "Save JSON", Save, "");
        var quitKey = new Shortcut(Key.Q.WithCtrl, "Quit", Quit, "");
        var bar = new StatusBar(new[] { _runKey, _pauseKey, _stopKey, _saveKey, quitKey }) { Y = Pos.AnchorEnd(1) };
        Add(_tabs, bar);
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool running = _runner != null && !_runner.IsFinished;
        _runKey.Enabled = !running;
        _pauseKey.Enabled = running;
        _pauseKey.CommandView.Text = _runner is { IsPaused: true } ? "Resume" : "Pause";
        _stopKey.Enabled = running;
        _saveKey.Enabled = _shown != null;
        _settings.SetEnabledAll(!running);
        _blocks.SetEnabledAll(!running);
    }

    private void Run()
    {
        if (_runner != null && !_runner.IsFinished) return;
        var s = new ClassicSettings();
        bool useNet;
        int seed;
        try
        {
            _settings.ApplyTo(s);
            _blocks.ApplyTo(s);
            useNet = _settings.UseNet;
            seed = _settings.Seed;
        }
        catch (ArgumentException e)
        {
            MessageBox.ErrorQuery(_app, "Invalid settings", e.Message, "OK");
            return;
        }
        DisposeRunner();
        _activeSettings = s;
        _fuelName = _settings.FuelName;
        _shown = new ClassicSample(s.SizeX, s.SizeY, s.SizeZ);
        _runner = new ClassicRunner(s, useNet, seed);
        _runner.Start();
        _tabs.Value = _run;
        _run.ShowProgress($"Starting (seed {seed})…");
        _timer ??= _app.AddTimeout(TimeSpan.FromMilliseconds(100), Poll);
        UpdateControls();
    }

    private bool Poll()
    {
        if (_runner == null) return true;
        if (_runner.Error != null)
        {
            var err = _runner.Error;
            DisposeRunner();
            _run.ShowProgress("Optimizer crashed: " + err.Message);
            MessageBox.ErrorQuery(_app, "Optimizer error", err.ToString(), "OK");
            UpdateControls();
            return true;
        }
        if (_shown != null && _runner.TryTakeSnapshot(_shown))
            _run.ShowSample(_shown);
        if (_runner.TryTakeLossHistory(_loss))
            _run.ShowLoss(_loss, _run.LossWidth);
        var p = _runner.Progress;
        string stage = p.Stage switch
        {
            ClassicOpt.StageTrain => $"Episode {p.Episode}, training iteration {p.Iteration}",
            ClassicOpt.StageInfer => $"Episode {p.Episode}, inference iteration {p.Iteration}",
            _ => $"Episode {p.Episode}, stage {p.Stage}, iteration {p.Iteration}",
        };
        string state = p.Finished ? "Stopped" : p.Paused ? "Paused" : $"{p.StepsPerSecond:0} steps/s";
        _run.ShowProgress($"{stage}  —  {state}  (seed {_runner.Seed}, {p.Steps:N0} steps)");
        if (p.Finished) UpdateControls();
        return true;
    }

    private void TogglePause()
    {
        if (_runner == null || _runner.IsFinished) return;
        if (_runner.IsPaused) _runner.Resume(); else _runner.Pause();
        UpdateControls();
    }

    private void Stop()
    {
        if (_runner == null) return;
        _runner.Stop();
        Poll(); // publish the final best
        UpdateControls();
    }

    private void Save()
    {
        if (_shown == null || _activeSettings == null) return;
        var dialog = new SaveDialog { Title = "Save Hellrage Reactor Planner JSON" };
        dialog.Path = Path.Combine(Environment.CurrentDirectory, "reactor.json");
        _app.Run(dialog);
        if (dialog.Canceled || string.IsNullOrWhiteSpace(dialog.Path)) return;
        try
        {
            File.WriteAllText(dialog.Path, ClassicExport.ToHellrageJson(_activeSettings, _shown.State, _fuelName));
            _run.ShowProgress($"Saved {dialog.Path}");
        }
        catch (Exception e)
        {
            MessageBox.ErrorQuery(_app, "Save failed", e.Message, "OK");
        }
    }

    private void Quit()
    {
        DisposeRunner();
        _app.RequestStop();
    }

    private void DisposeRunner()
    {
        _runner?.Stop();
        _runner?.Dispose();
        _runner = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_timer != null) _app.RemoveTimeout(_timer);
            DisposeRunner();
        }
        base.Dispose(disposing);
    }
}
