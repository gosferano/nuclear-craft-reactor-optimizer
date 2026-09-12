using System.Diagnostics;

namespace FissionOpt.Core;

/// <summary>Progress counters published by <see cref="OptimizerRunner{TSample}"/> for display.</summary>
public readonly record struct RunnerProgress(int Episode, int Stage, int Iteration, long Steps, double StepsPerSecond, bool Paused, bool Finished, bool Preparing = false);

/// <summary>
/// Runs an optimizer on a dedicated background thread and publishes the best design as a snapshot
/// the UI thread can copy. The optimizer itself stays single-threaded, exactly like the C++; only
/// the snapshot, the loss history and the progress counters cross threads.
/// </summary>
public sealed class OptimizerRunner<TSample> : IDisposable where TSample : class
{
    private readonly Func<IOptimizer<TSample>> _factory;
    private readonly int _seed;
    private IOptimizer<TSample>? _opt;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private TSample? _snapshot;
    private double[] _lossSnapshot = Array.Empty<double>();
    private long _snapshotVersion, _takenVersion;
    private bool _lossChanged;
    private volatile bool _paused, _stopRequested, _finished;
    private RunnerProgress _progress;
    private Exception? _error;

    public int Seed => _seed;
    public bool IsPaused => _paused;
    public bool IsFinished => _finished;
    /// <summary>The optimizer, once the thread has constructed it (null while preparing).</summary>
    public IOptimizer<TSample>? Optimizer => Volatile.Read(ref _opt);
    /// <summary>Set if the optimizer thread died with an exception.</summary>
    public Exception? Error => _error;

    public OptimizerRunner(IOptimizer<TSample> opt) : this(() => opt, opt.Seed) { }

    /// <summary>
    /// Constructs the optimizer on the background thread via <paramref name="factory"/>, so expensive setup
    /// (e.g. building a unit pool) never blocks the caller; <see cref="RunnerProgress.Preparing"/> is true until then.
    /// </summary>
    public OptimizerRunner(Func<IOptimizer<TSample>> factory, int seed)
    {
        _factory = factory;
        _seed = seed;
        _progress = new RunnerProgress(0, 0, 0, 0, 0, false, false, Preparing: true);
        // The evaluators use explicit stacks, but a roomy stack costs nothing and guards the net's loops too.
        _thread = new Thread(Loop, 64 * 1024 * 1024) { IsBackground = true, Name = "FissionOpt optimizer" };
    }

    public void Start() => _thread.Start();
    public void Pause() => _paused = true;

    public void Resume()
    {
        _paused = false;
        lock (_lock) Monitor.PulseAll(_lock);
    }

    /// <summary>Asks the thread to finish and waits for it.</summary>
    public void Stop()
    {
        _stopRequested = true;
        Resume();
        if (_thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join();
    }

    public RunnerProgress Progress
    {
        get { lock (_lock) return _progress; }
    }

    /// <summary>Copies the latest best design into <paramref name="dest"/> if it changed since the last call.</summary>
    public bool TryTakeSnapshot(TSample dest)
    {
        lock (_lock)
        {
            if (_snapshot == null || _snapshotVersion == _takenVersion) return false;
            _opt!.CopySample(_snapshot, dest);
            _takenVersion = _snapshotVersion;
            return true;
        }
    }

    /// <summary>Copies the loss history if it changed since the last call.</summary>
    public bool TryTakeLossHistory(double[] dest)
    {
        lock (_lock)
        {
            if (!_lossChanged) return false;
            Array.Copy(_lossSnapshot, dest, _lossSnapshot.Length);
            _lossChanged = false;
            return true;
        }
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long steps = 0, lastSteps = 0;
        double lastTime = 0, rate = 0;
        try
        {
            var opt = _factory();
            var snapshot = opt.CreateSample();
            opt.CopySample(opt.Best, snapshot);
            lock (_lock)
            {
                _lossSnapshot = new double[opt.LossHistory.Length];
                _snapshot = snapshot;
                _snapshotVersion = 1;
                Volatile.Write(ref _opt, opt);
                _progress = new RunnerProgress(opt.NEpisode, opt.NStage, opt.NIteration, 0, 0, false, false);
            }
            sw.Restart();
            while (!_stopRequested)
            {
                if (_paused)
                {
                    lock (_lock)
                    {
                        _progress = _progress with { Paused = true };
                        while (_paused && !_stopRequested) Monitor.Wait(_lock, 100);
                    }
                    sw.Restart(); lastTime = 0; lastSteps = steps;
                    continue;
                }
                opt.Step();
                ++steps;
                bool redraw = opt.NeedsRedrawBest();
                bool loss = opt.NeedsReplotLoss();
                if (redraw || loss || (steps & 255) == 0)
                {
                    double t = sw.Elapsed.TotalSeconds;
                    if (t - lastTime >= 0.5)
                    {
                        rate = (steps - lastSteps) / (t - lastTime);
                        lastTime = t; lastSteps = steps;
                    }
                    lock (_lock)
                    {
                        if (redraw)
                        {
                            opt.CopySample(opt.Best, snapshot);
                            ++_snapshotVersion;
                        }
                        if (loss)
                        {
                            opt.LossHistory.CopyTo(_lossSnapshot);
                            _lossChanged = true;
                        }
                        _progress = new RunnerProgress(opt.NEpisode, opt.NStage, opt.NIteration, steps, rate, false, false);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _error = e;
        }
        finally
        {
            lock (_lock)
            {
                // Publish whatever is best at shutdown, even if it never crossed the redraw throttle.
                if (_opt != null && _snapshot != null)
                {
                    _opt.CopySample(_opt.Best, _snapshot);
                    ++_snapshotVersion;
                }
                _finished = true;
                _progress = _progress with { Finished = true, Paused = false, Preparing = false };
            }
        }
    }

    public void Dispose()
    {
        Stop();
        (_opt as IDisposable)?.Dispose();
    }
}
