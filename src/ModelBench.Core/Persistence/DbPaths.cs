namespace ModelBench.Core.Persistence;

public static class DbPaths
{
    /// <summary>--db value, else MODELBENCH_DB, else ~/.modelbench/modelbench.db. Creates the directory.</summary>
    public static string Resolve(string? cliPath)
    {
        var env = Environment.GetEnvironmentVariable("MODELBENCH_DB");
        var path = !string.IsNullOrWhiteSpace(cliPath) ? cliPath
            : !string.IsNullOrWhiteSpace(env) ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".modelbench", "modelbench.db");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
