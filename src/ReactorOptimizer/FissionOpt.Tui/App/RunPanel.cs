using FissionOpt.Core;
using FissionOpt.Core.Classic;
using FissionOpt.Core.Overhaul;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace FissionOpt.Tui.App;

/// <summary>The "Reactor" tab: progress line, the live design, and the metrics / block-count panels.</summary>
public sealed class RunPanel : View
{
    private readonly Label _progress;
    private readonly ReactorView _reactor;
    private readonly Label _metrics;
    private readonly Label _blocks;
    private readonly Label _loss;

    public RunPanel()
    {
        Title = "_Reactor";
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true; // containers must be focusable for their fields to receive focus

        _progress = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Text = "Not running. Configure settings and press F5 to run." };
        var reactorFrame = new FrameView { Title = "Design", X = 0, Y = 1, Width = Dim.Fill(36), Height = Dim.Fill() };
        _reactor = new ReactorView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        reactorFrame.Add(_reactor);
        var side = new View { X = Pos.Right(reactorFrame), Y = 1, Width = 36, Height = Dim.Fill() };
        var metricsFrame = new FrameView { Title = "Metrics", X = 0, Y = 0, Width = Dim.Fill(), Height = 10 };
        _metrics = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = "" };
        metricsFrame.Add(_metrics);
        var lossFrame = new FrameView { Title = "Training loss", X = 0, Y = Pos.Bottom(metricsFrame), Width = Dim.Fill(), Height = 3 };
        _loss = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "" };
        lossFrame.Add(_loss);
        var blocksFrame = new FrameView { Title = "Blocks", X = 0, Y = Pos.Bottom(lossFrame), Width = Dim.Fill(), Height = Dim.Fill() };
        _blocks = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = "" };
        blocksFrame.Add(_blocks);
        side.Add(metricsFrame, lossFrame, blocksFrame);
        Add(_progress, reactorFrame, side);
    }

    public void SetMode(Func<int, string> label, Func<int, Color, Attribute> style) => _reactor.SetMode(label, style);

    public void ShowSample(ClassicSample sample)
    {
        _reactor.SetState(sample.State);
        _metrics.Text = ClassicExport.RenderMetrics(sample.Value).TrimEnd();
        _blocks.Text = TrimCounts(ClassicExport.RenderBlockCounts(sample.State));
    }

    public void ShowSample(OverhaulSettings settings, OverhaulSample sample)
    {
        _reactor.SetState(sample.State);
        _metrics.Text = OverhaulExport.RenderMetrics(sample.Value).TrimEnd();
        _blocks.Text = TrimCounts(OverhaulExport.RenderBlockCounts(settings, sample.State));
    }

    public void Clear()
    {
        _reactor.SetState(null);
        _metrics.Text = "";
        _blocks.Text = "";
        _loss.Text = "";
    }

    private static string TrimCounts(string rendered) =>
        string.Join('\n', rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Trim()));

    public void ShowProgress(string text) => _progress.Text = text;

    private static readonly char[] Bars = { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };

    /// <summary>Sparkline of the most recent losses (each value relative to the window's max).</summary>
    public void ShowLoss(double[] history, int width)
    {
        width = Math.Max(8, width);
        int n = Math.Min(width, history.Length);
        double max = 0;
        for (int i = history.Length - n; i < history.Length; ++i) max = Math.Max(max, history[i]);
        if (max <= 0) { _loss.Text = ""; return; }
        var chars = new char[n];
        for (int i = 0; i < n; ++i)
        {
            double v = history[history.Length - n + i] / max;
            chars[i] = Bars[Math.Clamp((int)(v * (Bars.Length - 1) + 0.5), 0, Bars.Length - 1)];
        }
        _loss.Text = new string(chars) + $" {history[^1]:0.####}";
    }

    public int LossWidth => Math.Max(8, _loss.Viewport.Width - 10);
}
