using System.Diagnostics;

namespace FissionOpt.Core;

/// <summary>Progress counters published by <see cref="OptimizerRunner{TSample}"/> for display.</summary>
public readonly record struct RunnerProgress(int Episode, int Stage, int Iteration, long Steps, double StepsPerSecond, bool Paused, bool Finished);

/// <summary>
/// Runs an optimizer on a dedicated background thread and publishes the best design as a snapshot
/// the UI thread can copy. The optimizer itself stays single-threaded, exactly like the C++; only
/// the snapshot, the loss history and the progress counters cross threads.
/// </summary>
public sealed class OptimizerRunner<TSample> : IDisposable where TSample : class
{
    private readonly IOptimizer<TSample> _opt;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly TSample _snapshot;
    private readonly double[] _lossSnapshot;
    private long _snapshotVersion, _takenVersion;
    private bool _lossChanged;
    private volatile bool _paused, _stopRequested, _finished;
    private RunnerProgress _progress;
    private Exception? _error;

    public int Seed => _opt.Seed;
    public bool IsPaused => _paused;
    public bool IsFinished => _finished;
    /// <summary>Set if the optimizer thread died with an exception.</summary>
    public Exception? Error => _error;

    public OptimizerRunner(IOptimizer<TSample> opt)
    {
        _opt = opt;
        _snapshot = opt.CreateSample();
        opt.CopySample(opt.Best, _snapshot);
        _snapshotVersion = 1;
        _lossSnapshot = new double[opt.LossHistory.Length];
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
            if (_snapshotVersion == _takenVersion) return false;
            _opt.CopySample(_snapshot, dest);
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
                _opt.Step();
                ++steps;
                bool redraw = _opt.NeedsRedrawBest();
                bool loss = _opt.NeedsReplotLoss();
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
                            _opt.CopySample(_opt.Best, _snapshot);
                            ++_snapshotVersion;
                        }
                        if (loss)
                        {
                            _opt.LossHistory.CopyTo(_lossSnapshot);
                            _lossChanged = true;
                        }
                        _progress = new RunnerProgress(_opt.NEpisode, _opt.NStage, _opt.NIteration, steps, rate, false, false);
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
                _opt.CopySample(_opt.Best, _snapshot);
                ++_snapshotVersion;
                _finished = true;
                _progress = _progress with { Finished = true, Paused = false };
            }
        }
    }

    public void Dispose()
    {
        Stop();
        (_opt as IDisposable)?.Dispose();
    }
}
