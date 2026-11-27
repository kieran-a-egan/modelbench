using System.CommandLine;
using ModelBench.Core.Config;
using ModelBench.Core.Grading;
using ModelBench.Core.Persistence;
using ModelBench.Core.Reporting;

namespace ModelBench.Cli.Commands;

/// <summary>Global options plus helpers shared by all commands.</summary>
internal sealed class CliContext
{
    public Option<string?> Db { get; } = new("--db") { Description = "SQLite database path (default: MODELBENCH_DB, then ~/.modelbench/modelbench.db)", Recursive = true };
    public Option<string?> Models { get; } = new("--models") { Description = "models.json path (default: MODELBENCH_MODELS, then ./models.json, then ~/.modelbench/models.json)", Recursive = true };

    public BenchDb OpenDb(ParseResult pr) => BenchDb.Open(DbPaths.Resolve(pr.GetValue(Db)));

    public ModelCatalog Catalog(ParseResult pr) => ModelCatalog.Load(pr.GetValue(Models));

    /// <summary>Runs a command body, turning expected user/infrastructure errors into "error: ..." and exit code 1.</summary>
    public static async Task<int> Guard(ParseResult pr, Func<TextWriter, Task<int>> body)
    {
        try { return await body(pr.InvocationConfiguration.Output); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or NotSupportedException or InfraException)
        {
            await pr.InvocationConfiguration.Error.WriteLineAsync("error: " + ConsoleSafe.Sanitize(ex.Message));
            return 1;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
        {
            await pr.InvocationConfiguration.Error.WriteLineAsync("error: " + ConsoleSafe.Sanitize(ex.Message));
            return 1;
        }
        catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            await pr.InvocationConfiguration.Error.WriteLineAsync("error: timed out: " + ConsoleSafe.Sanitize(ex.InnerException.Message));
            return 1;
        }
        catch (OperationCanceledException)
        {
            await pr.InvocationConfiguration.Error.WriteLineAsync("error: cancelled");
            return 130;
        }
    }

    /// <summary>Accepts a full Guid or any unique prefix of one.</summary>
    public static Guid ResolveId(string text, IEnumerable<Guid> ids, string what)
    {
        var prefix = text.Trim().ToLowerInvariant();
        var matches = ids.Where(i => i.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"no {what} matches '{ConsoleSafe.Sanitize(text)}'"),
            _ => throw new ArgumentException($"'{ConsoleSafe.Sanitize(text)}' matches {matches.Count} {what}s; use a longer prefix"),
        };
    }

    public static string S(string? text) => ConsoleSafe.Sanitize(text);

    public static string Short(Guid id) => id.ToString()[..8];

    public static string Truncate(string? text, int max)
    {
        var s = S(text);
        return s.Length > max ? s[..max] + "..." : s;
    }

    public static void Table(TextWriter w, string[] headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        w.WriteLine(Line(headers));
        foreach (var r in rows) w.WriteLine(Line(r));
    }
}
