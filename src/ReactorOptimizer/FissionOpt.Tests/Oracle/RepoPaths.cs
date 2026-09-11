namespace FissionOpt.Tests.Oracle;

internal static class RepoPaths
{
    private static readonly Lazy<string> _root = new(FindRoot);

    /// <summary>Repository root: the nearest ancestor of the test binary containing reference/oracle/Oracle.cpp.</summary>
    public static string Root => _root.Value;

    public static string OracleDir => Path.Combine(Root, "reference", "oracle");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "reference", "oracle", "Oracle.cpp")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (reference/oracle/Oracle.cpp) above " + AppContext.BaseDirectory);
    }
}
