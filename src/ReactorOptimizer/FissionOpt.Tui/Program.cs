using FissionOpt.Tui.App;
using FissionOpt.Tui.Headless;
using Terminal.Gui.App;

if (args.Length > 0 && (args[0] == "headless" || args[0] == "--headless"))
{
    try
    {
        return ClassicHeadless.Run(args[1..]);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine("error: " + e.Message);
        Console.Error.WriteLine(ClassicHeadless.Usage);
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
