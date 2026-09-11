using System.Diagnostics;

namespace FissionOpt.Core.Classic;

/// <summary>Progress counters published by <see cref="ClassicRunner"/> for display.</summary>
public readonly record struct RunnerProgress(int Episode, int Stage, int Iteration, long Steps, double StepsPerSecond, bool Paused, bool Finished);

/// <summary>
/// Runs a <see cref="ClassicOpt"/> on a dedicated background thread and publishes the best design
/// as a snapshot the UI thread can copy. The optimizer itself stays single-threaded, exactly like
/// the C++; only the snapshot and the progress counters cross threads.
/// </summary>
public sealed class ClassicRunner : IDisposable
{
    private readonly ClassicOpt _opt;
    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly ClassicSample _snapshot;
    private readonly double[] _lossSnapshot = new double[ClassicOpt.NLossHistory];
    private long _snapshotVersion, _takenVersion;
    private bool _lossChanged;
    private volatile bool _paused, _stopRequested, _finished;
    private RunnerProgress _progress;
    private Exception? _error;

    public ClassicSettings Settings => _opt.Settings;
    public int Seed => _opt.Seed;
    public bool IsPaused => _paused;
    public bool IsFinished => _finished;
    /// <summary>Set if the optimizer thread died with an exception.</summary>
    public Exception? Error => _error;

    public ClassicRunner(ClassicSettings settings, bool useNet, int seed)
    {
        _opt = new ClassicOpt(settings, useNet, seed);
        _snapshot = new ClassicSample(settings.SizeX, settings.SizeY, settings.SizeZ);
        _snapshot.CopyFrom(_opt.Best);
        _snapshotVersion = 1;
        // Nothing here recurses deeply, but give the optimizer room anyway; it costs nothing.
        _thread = new Thread(Loop, 64 * 1024 * 1024) { IsBackground = true, Name = "FissionOpt classic" };
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
    public bool TryTakeSnapshot(ClassicSample dest)
    {
        lock (_lock)
        {
            if (_snapshotVersion == _takenVersion) return false;
            dest.CopyFrom(_snapshot);
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
            Array.Copy(_lossSnapshot, dest, ClassicOpt.NLossHistory);
            _lossChanged = false;
            return true;
        }
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long steps = 0, lastSteps = 0;
        double lastTime = 0;
        double rate = 0;
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
                            _snapshot.CopyFrom(_opt.Best);
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
                _snapshot.CopyFrom(_opt.Best);
                ++_snapshotVersion;
                _finished = true;
                _progress = _progress with { Finished = true, Paused = false };
            }
        }
    }

    public void Dispose() => Stop();
}
