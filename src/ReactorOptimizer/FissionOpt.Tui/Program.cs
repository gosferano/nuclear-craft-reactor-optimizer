using FissionOpt.Tui.App;
using FissionOpt.Tui.Headless;
using Terminal.Gui.App;

if (args.Length > 0 && (args[0] == "headless" || args[0] == "--headless"))
{
    var rest = args[1..];
    int modeIdx = Array.IndexOf(rest, "--mode");
    bool overhaul = modeIdx >= 0 && modeIdx + 1 < rest.Length && rest[modeIdx + 1].Equals("overhaul", StringComparison.OrdinalIgnoreCase);
    try
    {
        return overhaul ? OverhaulHeadless.Run(rest) : ClassicHeadless.Run(rest);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine("error: " + e.Message);
        Console.Error.WriteLine(overhaul ? OverhaulHeadless.Usage : ClassicHeadless.Usage);
        return 2;
    }
}

if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
{
    Console.WriteLine("FissionOpt.Tui            interactive terminal UI");
    Console.WriteLine("FissionOpt.Tui headless   run an optimization from the command line (see `headless --help`)");
    return 0;
}

using IApplication app = Application.Create();
app.Init();
using var window = new MainWindow(app);
app.Run(window);
return 0;
