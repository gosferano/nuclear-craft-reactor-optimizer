using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FissionOpt.Tests.Oracle;

/// <summary>
/// Owns one long-lived instance of the C++ evaluation oracle (reference/oracle/Oracle.cpp) and
/// speaks its line protocol. Builds the binary on first use if it is missing or older than its
/// sources. One process is shared per test collection; spawning per case would be far too slow.
/// </summary>
public sealed class OracleProcess : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _in;
    private readonly StreamReader _out;
    private readonly object _lock = new();

    public OracleProcess()
    {
        string exe = EnsureBuilt();
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _proc.Start();
        _in = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false), 1 << 16) { AutoFlush = false };
        _out = new StreamReader(_proc.StandardOutput.BaseStream, Encoding.UTF8, false, 1 << 16);
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine("[oracle] " + e.Data); };
        _proc.BeginErrorReadLine();
    }

    /// <summary>Sends one request (already formatted) and returns the response lines up to, excluding, "END".</summary>
    public List<string> Exchange(string request)
    {
        lock (_lock)
        {
            _in.Write(request);
            _in.Flush();
            var lines = new List<string>();
            while (true)
            {
                string? line = _out.ReadLine();
                if (line == null)
                    throw new InvalidOperationException("oracle exited unexpectedly (exit code " + SafeExitCode() + ")");
                if (line == "END") return lines;
                lines.Add(line);
            }
        }
    }

    private string SafeExitCode()
    {
        try { return _proc.HasExited ? _proc.ExitCode.ToString(CultureInfo.InvariantCulture) : "still running"; }
        catch { return "?"; }
    }

    private static string EnsureBuilt()
    {
        string dir = RepoPaths.OracleDir;
        string exe = Path.Combine(dir, "build", OperatingSystem.IsWindows() ? "oracle.exe" : "oracle");
        string fissionDir = Path.Combine(RepoPaths.Root, "reference", "FissionOpt");
        string[] sources =
        {
            Path.Combine(dir, "Oracle.cpp"),
            Path.Combine(fissionDir, "Fission.cpp"),
            Path.Combine(fissionDir, "Fission.h"),
            Path.Combine(fissionDir, "OverhaulFission.cpp"),
            Path.Combine(fissionDir, "OverhaulFission.h"),
        };
        foreach (var s in sources)
            if (!File.Exists(s))
                throw new FileNotFoundException(
                    "Oracle source missing. Did you run `git submodule update --init`?", s);

        bool stale = !File.Exists(exe) || sources.Any(s => File.GetLastWriteTimeUtc(s) > File.GetLastWriteTimeUtc(exe));
        if (!stale) return exe;

        var psi = new ProcessStartInfo("sh", new[] { Path.Combine(dir, "build.sh") })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = dir,
        };
        using var build = Process.Start(psi)!;
        string stdout = build.StandardOutput.ReadToEnd();
        string stderr = build.StandardError.ReadToEnd();
        build.WaitForExit();
        if (build.ExitCode != 0 || !File.Exists(exe))
            throw new InvalidOperationException("Building the C++ oracle failed:\n" + stdout + "\n" + stderr);
        return exe;
    }

    public void Dispose()
    {
        try
        {
            lock (_lock)
            {
                _in.Write("quit\n");
                _in.Flush();
            }
            if (!_proc.WaitForExit(2000)) _proc.Kill();
        }
        catch { /* best effort */ }
        _proc.Dispose();
    }
}

/// <summary>xunit collection so every differential test class shares one oracle process.</summary>
[CollectionDefinition(Name)]
public sealed class OracleCollection : ICollectionFixture<OracleProcess>
{
    public const string Name = "oracle";
}
