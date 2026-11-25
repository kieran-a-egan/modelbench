using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Persistence;

namespace ModelBench.Core.Reporting;

/// <summary>JSON and CSV export of one run. Snapshots are already secret-stripped; candidate text is untrusted, so CSV cells are formula-neutralised.</summary>
public static class Export
{
    public static readonly IReadOnlyList<string> Tables = ["runs", "attempts", "assertions"];

    private static readonly string[] MetricColumns =
    [
        MetricNames.InputTokens, MetricNames.OutputTokens, MetricNames.ReasoningTokens, MetricNames.TimeToFirstTokenMs, MetricNames.ThroughputTps,
        MetricNames.DurationMs, MetricNames.ModelCalls, MetricNames.CostUsd, MetricNames.EnergyKwh, MetricNames.EnergyCostUsd,
    ];

    private static readonly JsonSerializerOptions Indented = new(JsonUtil.Options) { WriteIndented = true };

    private static Run Load(BenchDb db, Guid runId)
    {
        var run = db.Runs.AsNoTracking().Include(r => r.Suite).FirstOrDefault(r => r.Id == runId) ?? throw new InvalidOperationException($"run {runId} not found");
        run.Attempts = db.Attempts.AsNoTracking().Include(a => a.Task).Include(a => a.Assertions).Include(a => a.Metrics)
            .Where(a => a.RunId == runId).ToList().OrderBy(a => a.Task!.Index).ThenBy(a => a.RepeatIndex).ToList();
        return run;
    }

    private static JsonNode? Node(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }

    public static string Json(BenchDb db, Guid runId)
    {
        var run = Load(db, runId);
        return JsonSerializer.Serialize(new
        {
            run = new
            {
                run.Id, run.SuiteId, suiteName = run.Suite!.Name, suiteHash = run.Suite.ContentHash, run.ModelRef, run.Mode, run.Status, run.Repeats, run.Concurrency,
                run.TimeoutSeconds, run.Label, run.CreatedAt, run.CompletedAt,
                modelSnapshot = Node(run.ModelSnapshotJson), retryPolicy = Node(run.RetryPolicyJson), environment = Node(run.EnvironmentJson),
            },
            attempts = run.Attempts.Select(a => new
            {
                a.Id, taskIndex = a.Task!.Index, family = a.Task.FamilyId, category = a.Task.Category, complexity = a.Task.Complexity, repeat = a.RepeatIndex,
                a.Outcome, a.Score, a.ErrorMessage, a.Retries, a.StartedAt, a.CompletedAt, a.FinalText,
                rawRequests = Node(a.RawRequestsJson), rawResponses = Node(a.RawResponsesJson), transcript = Node(a.TranscriptJson),
                assertions = a.Assertions.OrderBy(x => x.Id).Select(x => new { x.Grader, x.Name, x.Expected, x.Actual, x.Score, x.Weight, x.Required, x.Passed, x.Diagnostic }),
                metrics = a.Metrics.OrderBy(m => m.Id).Select(m => new { m.Name, m.Value, m.Unit, m.Provenance }),
            }),
        }, Indented);
    }

    public static void WriteCsv(BenchDb db, Guid runId, string table, TextWriter w)
    {
        var run = Load(db, runId);
        switch (table)
        {
            case "runs":
                var s = Reports.Summarize(db, runId);
                Row(w, "run_id", "suite_id", "suite_name", "suite_hash", "model_ref", "mode", "status", "repeats", "timeout_seconds", "label", "created_at", "completed_at",
                    "attempts", "graded", "passed", "infra_errors", "timeouts", "cancelled", "pass_rate", "mean_score", "consistency", "cost_per_success");
                Row(w, run.Id, run.SuiteId, run.Suite!.Name, run.Suite.ContentHash, run.ModelRef, run.Mode, run.Status, run.Repeats, run.TimeoutSeconds, run.Label, run.CreatedAt, run.CompletedAt,
                    s.Attempts, s.Graded, s.Passed, s.InfraErrors, s.Timeouts, s.Cancelled, s.PassRate, s.MeanScore, s.Consistency, s.CostPerSuccess);
                break;
            case "attempts":
                Row(w, [.. new[] { "attempt_id", "run_id", "task_index", "family", "category", "complexity", "repeat", "outcome", "score", "retries", "error", "final_text" }, .. MetricColumns]);
                foreach (var a in run.Attempts)
                    Row(w, [a.Id, run.Id, a.Task!.Index, a.Task.FamilyId, a.Task.Category, a.Task.Complexity, a.RepeatIndex, a.Outcome, a.Score, a.Retries, a.ErrorMessage, a.FinalText,
                        .. MetricColumns.Select(n => (object?)a.Metrics.FirstOrDefault(m => m.Name == n)?.Value)]);
                break;
            case "assertions":
                Row(w, "attempt_id", "task_index", "repeat", "grader", "name", "expected", "actual", "score", "weight", "required", "passed", "diagnostic");
                foreach (var a in run.Attempts)
                    foreach (var x in a.Assertions.OrderBy(x => x.Id))
                        Row(w, a.Id, a.Task!.Index, a.RepeatIndex, x.Grader, x.Name, x.Expected, x.Actual, x.Score, x.Weight, x.Required, x.Passed, x.Diagnostic);
                break;
            default: throw new ArgumentException($"unknown table '{table}' (known: {string.Join(", ", Tables)})");
        }
    }

    /// <summary>Writes runs.csv, attempts.csv and assertions.csv into a directory and returns their paths.</summary>
    public static List<string> WriteCsvDirectory(BenchDb db, Guid runId, string dir)
    {
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        foreach (var t in Tables)
        {
            var path = Path.Combine(dir, t + ".csv");
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            WriteCsv(db, runId, t, w);
            paths.Add(path);
        }
        return paths;
    }

    private static void Row(TextWriter w, params object?[] cells) => w.Write(string.Join(",", cells.Select(Cell)) + "\r\n");

    /// <summary>RFC 4180 quoting; text starting with = + - @ tab or CR gets a leading single quote so spreadsheets do not run it as a formula.</summary>
    public static string Cell(object? v)
    {
        switch (v)
        {
            case null: return "";
            case string s:
                if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
                return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            case bool b: return b ? "true" : "false";
            case DateTimeOffset t: return t.ToString("O", CultureInfo.InvariantCulture);
            default: return Convert.ToString(v, CultureInfo.InvariantCulture)!;
        }
    }
}
