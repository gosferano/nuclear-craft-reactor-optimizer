using FissionOpt.Tui.Headless;

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

Console.Error.WriteLine("The interactive TUI is not implemented yet. Run `FissionOpt.Tui headless --help` for the headless mode.");
return 1;
