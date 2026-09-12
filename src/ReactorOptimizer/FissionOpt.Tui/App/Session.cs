using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Overhaul;

namespace FissionOpt.Tui.App;

/// <summary>One optimization run (either mode) as seen by <see cref="MainWindow"/>: control, progress, display, export.</summary>
public interface ISession : IDisposable
{
    int Seed { get; }
    bool IsPaused { get; }
    bool IsFinished { get; }
    Exception? Error { get; }
    void Start();
    void Pause();
    void Resume();
    void Stop();
    RunnerProgress Progress { get; }
    string StageText(RunnerProgress p);
    /// <summary>Pushes the latest best design into the panel if it changed; returns true if it did.</summary>
    bool RefreshBest(RunPanel panel);
    bool TryTakeLossHistory(double[] dest);
    bool HasDesign { get; }
    string ExportJson();
    void ConfigurePanel(RunPanel panel);
}

public sealed class ClassicSession : ISession
{
    private readonly ClassicSettings _settings;
    private readonly string _fuelName;
    private readonly OptimizerRunner<ClassicSample> _runner;
    private readonly ClassicSample _shown;
    public bool HasDesign { get; private set; }

    public ClassicSession(ClassicSettings settings, bool useNet, int seed, string fuelName, bool? incremental, bool simdNet)
    {
        _settings = settings;
        _fuelName = fuelName;
        // The runner constructs the optimizer on its own thread so the UI never blocks on setup.
        _runner = new OptimizerRunner<ClassicSample>(() =>
            new ClassicOpt(settings, useNet, seed, incrementalEvaluation: incremental, simdNet: simdNet), seed);
        _shown = new ClassicSample(settings.SizeX, settings.SizeY, settings.SizeZ);
    }

    private string ModeText
    {
        get
        {
            if (_runner.Optimizer is not ClassicOpt opt) return "";
            return (opt.IncrementalEvaluation ? " [incremental]" : opt.ParallelChildren ? " [4 threads]" : "")
                + (opt.UsesNet ? opt.SimdNet ? " [simd net]" : " [scalar net]" : "");
        }
    }

    public int Seed => _runner.Seed;
    public bool IsPaused => _runner.IsPaused;
    public bool IsFinished => _runner.IsFinished;
    public Exception? Error => _runner.Error;
    public void Start() => _runner.Start();
    public void Pause() => _runner.Pause();
    public void Resume() => _runner.Resume();
    public void Stop() => _runner.Stop();
    public RunnerProgress Progress => _runner.Progress;
    public bool TryTakeLossHistory(double[] dest) => _runner.TryTakeLossHistory(dest);

    public string StageText(RunnerProgress p) => p.Preparing ? "Preparing…" : (p.Stage switch
    {
        ClassicOpt.StageTrain => $"Episode {p.Episode}, training iteration {p.Iteration}",
        ClassicOpt.StageInfer => $"Episode {p.Episode}, inference iteration {p.Iteration}",
        _ => $"Episode {p.Episode}, stage {p.Stage}, iteration {p.Iteration}",
    }) + ModeText;

    public void ConfigurePanel(RunPanel panel) => panel.SetMode(ClassicExport.Label, TileStyle.Classic);

    public bool RefreshBest(RunPanel panel)
    {
        if (!_runner.TryTakeSnapshot(_shown)) return false;
        HasDesign = true;
        panel.ShowSample(_shown);
        return true;
    }

    public string ExportJson() => ClassicExport.ToHellrageJson(_settings, _shown.State, _fuelName);
    public void Dispose() => _runner.Dispose();
}

public sealed class OverhaulSession : ISession
{
    private readonly OverhaulSettings _settings;
    private readonly OptimizerRunner<OverhaulSample> _runner;
    private readonly OverhaulSample _shown;
    public bool HasDesign { get; private set; }

    private readonly string _modeText;

    public OverhaulSession(OverhaulSettings settings, int seed, bool simdNet)
    {
        _settings = settings;
        var opt = new OverhaulOpt(settings, seed, simdNet); // calls settings.Compute()
        _modeText = opt.SimdNet ? " [simd net]" : " [scalar net]";
        _runner = new OptimizerRunner<OverhaulSample>(opt);
        _shown = new OverhaulSample(settings);
    }

    public int Seed => _runner.Seed;
    public bool IsPaused => _runner.IsPaused;
    public bool IsFinished => _runner.IsFinished;
    public Exception? Error => _runner.Error;
    public void Start() => _runner.Start();
    public void Pause() => _runner.Pause();
    public void Resume() => _runner.Resume();
    public void Stop() => _runner.Stop();
    public RunnerProgress Progress => _runner.Progress;
    public bool TryTakeLossHistory(double[] dest) => _runner.TryTakeLossHistory(dest);

    public string StageText(RunnerProgress p) => (p.Stage switch
    {
        OverhaulOpt.StageTrain => $"Episode {p.Episode}, training iteration {p.Iteration}",
        OverhaulOpt.StageInfer => $"Episode {p.Episode}, inference iteration {p.Iteration}",
        _ => $"Episode {p.Episode}, rollout iteration {p.Iteration}",
    }) + _modeText;

    public void ConfigurePanel(RunPanel panel) => panel.SetMode(t => OverhaulExport.Label(_settings, t), TileStyle.Overhaul);

    public bool RefreshBest(RunPanel panel)
    {
        if (!_runner.TryTakeSnapshot(_shown)) return false;
        HasDesign = true;
        panel.ShowSample(_settings, _shown);
        return true;
    }

    public string ExportJson() => OverhaulExport.ToPlannerJson(_settings, _shown.State);
    public void Dispose() => _runner.Dispose();
}
