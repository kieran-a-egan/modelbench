using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Persistence;

namespace ModelBench.Core.Reporting;

/// <summary>Aggregate of one metric over a run. Unavailable/null values are skipped (never 0); N/M is the coverage (ADR 0008).</summary>
public sealed record Agg(int N, int M, double Sum, double? Median)
{
    public double? Total => N > 0 ? Sum : null;
    public double? Mean => N > 0 ? Sum / N : null;
    public string Coverage => $"{N}/{M}";
}

/// <summary>Graded attempts sharing a key (category, complexity or family).</summary>
public sealed record Slice(string Key, int N, int Passed, double MeanScore)
{
    public double PassRate => N == 0 ? 0 : (double)Passed / N;
}

public sealed record RunSummary(
    Guid RunId, string ModelRef, ExecutionMode Mode, RunStatus Status, Guid SuiteId, string SuiteName, string SuiteHash,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? Label,
    IReadOnlyList<(AttemptOutcome Outcome, int Count)> Outcomes, int Attempts, int Graded, int Passed, int InfraErrors, int Timeouts, int Cancelled,
    double? PassRate, double? MeanScore, double? Consistency,
    IReadOnlyList<Slice> ByCategory, IReadOnlyList<Slice> ByComplexity, IReadOnlyList<Slice> ByFamily,
    IReadOnlyDictionary<string, Agg> Metrics, double? CostPerSuccess, int GradingVersion)
{
    public int Excluded => InfraErrors + Timeouts + Cancelled;
    /// <summary>Attempts the model is accountable for: graded ones plus timeouts (it ran out of its budget). Infra errors and cancellations are not.</summary>
    public int Accountable => Graded + Timeouts;
    /// <summary>Passed / (graded + timeouts): the ranking figure (MB-001). PassRate is graded accuracy only.</summary>
    public double? EndToEndRate => Accountable == 0 ? null : (double)Passed / Accountable;
    /// <summary>Share of attempts that are accountable; below 1 means infra errors or cancellations hid part of the run.</summary>
    public double? Completion => Attempts == 0 ? null : (double)Accountable / Attempts;
    public Agg Metric(string name) => Metrics.TryGetValue(name, out var a) ? a : new Agg(0, Attempts, 0, null);
}

public sealed record Matrix(IReadOnlyList<string> Models, IReadOnlyList<string> Columns, double?[][] Cells);
public sealed record LeaderboardGroup(string SuiteName, string SuiteHash, IReadOnlyList<RunSummary> Rows, int GradingVersion);
public sealed record BreakdownResult(Matrix ByCategory, Matrix ByComplexity);
public sealed record FailedAssertion(string Grader, string Name, string? Expected, string? Actual, string? Diagnostic, double Score, double Weight, bool Required);
public sealed record FailedAttempt(Guid AttemptId, int TaskIndex, string FamilyId, int Complexity, int Repeat, AttemptOutcome Outcome, double? Score, string? Error, IReadOnlyList<FailedAssertion> Assertions);

public enum TaskChange { PassToFail, FailToPass, BothPass, BothFail, Unscored }
public sealed record TaskDiff(int Index, string FamilyId, int Complexity, TaskChange Change, double? ScoreA, double? ScoreB)
{
    public double? ScoreDelta => ScoreA is { } a && ScoreB is { } b ? b - a : null;
}
public sealed record MetricDelta(string Name, double? A, double? B)
{
    public double? Delta => A is { } a && B is { } b ? b - a : null;
}
public sealed record Comparison(RunSummary A, RunSummary B, IReadOnlyList<TaskDiff> Tasks, IReadOnlyList<MetricDelta> Metrics)
{
    public double? EndToEndDelta => A.EndToEndRate is { } a && B.EndToEndRate is { } b ? b - a : null;
    public double? PassRateDelta => A.PassRate is { } a && B.PassRate is { } b ? b - a : null;
    public double? MeanScoreDelta => A.MeanScore is { } a && B.MeanScore is { } b ? b - a : null;
    public int Count(TaskChange c) => Tasks.Count(t => t.Change == c);
}

/// <summary>Pure queries over BenchDb shared by the CLI and the dashboard. Pass rate and mean score cover graded attempts only (ADR 0006).</summary>
public static class Reports
{
    private static readonly string[] MetricList =
    [
        MetricNames.InputTokens, MetricNames.OutputTokens, MetricNames.ReasoningTokens, MetricNames.TimeToFirstTokenMs, MetricNames.ThroughputTps,
        MetricNames.DurationMs, MetricNames.ModelCalls, MetricNames.CostUsd, MetricNames.EnergyKwh, MetricNames.EnergyCostUsd,
    ];

    private sealed record Row(Guid TaskId, int RepeatIndex, AttemptOutcome Outcome, double? Score, string Category, string FamilyId, int Complexity, int TaskIndex);

    private static List<Row> AttemptRows(BenchDb db, Guid runId) => db.Attempts.AsNoTracking().Where(a => a.RunId == runId)
        .Select(a => new Row(a.TaskId, a.RepeatIndex, a.Outcome, a.Score, a.Task!.Category, a.Task.FamilyId, a.Task.Complexity, a.Task.Index)).ToList();

    private static bool IsGraded(AttemptOutcome o) => o is AttemptOutcome.Passed or AttemptOutcome.Failed;

    private static List<Slice> Slices(IEnumerable<Row> graded, Func<Row, string> key) => graded.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new Slice(g.Key, g.Count(), g.Count(r => r.Outcome == AttemptOutcome.Passed), g.Average(r => r.Score ?? 0))).ToList();

    private static Agg Aggregate(IEnumerable<Metric> metrics, int total)
    {
        var v = metrics.Where(m => m.Value is not null && m.Provenance != Provenance.Unavailable).Select(m => m.Value!.Value).Order().ToList();
        double? median = v.Count == 0 ? null : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        return new Agg(v.Count, total, v.Sum(), median);
    }

    public static RunSummary Summarize(BenchDb db, Guid runId)
    {
        var run = db.Runs.AsNoTracking().Include(r => r.Suite).FirstOrDefault(r => r.Id == runId) ?? throw new InvalidOperationException($"run {runId} not found");
        var rows = AttemptRows(db, runId);
        var metrics = db.Metrics.AsNoTracking().Where(m => db.Attempts.Any(a => a.Id == m.AttemptId && a.RunId == runId)).ToList();
        var graded = rows.Where(r => IsGraded(r.Outcome)).ToList();
        var passed = graded.Count(r => r.Outcome == AttemptOutcome.Passed);
        var repeated = graded.GroupBy(r => r.TaskId).Where(g => g.Count() >= 2).ToList();
        var agg = MetricList.ToDictionary(n => n, n => Aggregate(metrics.Where(m => m.Name == n), rows.Count));
        var cost = agg[MetricNames.CostUsd];
        int Count(AttemptOutcome o) => rows.Count(r => r.Outcome == o);
        return new RunSummary(
            run.Id, run.ModelRef, run.Mode, run.Status, run.SuiteId, run.Suite!.Name, run.Suite.ContentHash, run.CreatedAt, run.CompletedAt, run.Label,
            Enum.GetValues<AttemptOutcome>().Select(o => (o, Count(o))).Where(t => t.Item2 > 0).ToList(),
            rows.Count, graded.Count, passed, Count(AttemptOutcome.InfraError), Count(AttemptOutcome.Timeout), Count(AttemptOutcome.Cancelled),
            graded.Count == 0 ? null : (double)passed / graded.Count,
            graded.Count == 0 ? null : graded.Average(r => r.Score ?? 0),
            repeated.Count == 0 ? null : (double)repeated.Count(g => g.Select(r => r.Outcome).Distinct().Count() == 1) / repeated.Count,
            Slices(graded, r => r.Category), Slices(graded, r => r.Complexity.ToString()), Slices(graded, r => r.FamilyId),
            agg, passed > 0 && cost.N == cost.M ? cost.Sum / passed : null, run.GradingVersion);
    }

    /// <summary>Completed runs of one mode, grouped by suite content hash (only comparable within a hash). Latest run per (model, hash) unless allRuns.</summary>
    public static List<LeaderboardGroup> Leaderboard(BenchDb db, ExecutionMode mode, Guid? suiteId = null, bool allRuns = false)
    {
        var hash = suiteId is { } id ? db.Suites.Where(s => s.Id == id).Select(s => s.ContentHash).FirstOrDefault() ?? throw new InvalidOperationException($"suite {id} not found") : null;
        return Groups(db, mode, hash, allRuns);
    }

    private static List<LeaderboardGroup> Groups(BenchDb db, ExecutionMode mode, string? hash, bool allRuns)
    {
        var runs = db.Runs.AsNoTracking().Include(r => r.Suite).Where(r => r.Mode == mode && r.Status == RunStatus.Completed).ToList()
            .Where(r => hash is null || r.Suite!.ContentHash == hash).ToList();
        if (!allRuns) runs = runs.GroupBy(r => (r.ModelRef, r.Suite!.ContentHash, r.GradingVersion)).Select(g => g.OrderByDescending(r => r.CreatedAt).First()).ToList();
        // Comparable only within one suite hash AND one grading methodology.
        return runs.GroupBy(r => (r.Suite!.ContentHash, r.GradingVersion))
            .Select(g => new LeaderboardGroup(g.First().Suite!.Name, g.Key.ContentHash, g.Select(r => Summarize(db, r.Id))
                .OrderByDescending(s => s.Completion == 1).ThenByDescending(s => s.EndToEndRate ?? -1).ThenByDescending(s => s.PassRate ?? -1).ThenByDescending(s => s.MeanScore ?? -1)
                .ThenBy(s => s.ModelRef, StringComparer.Ordinal).ThenByDescending(s => s.CreatedAt).ToList(), g.Key.GradingVersion))
            .OrderBy(g => g.SuiteName, StringComparer.Ordinal).ThenBy(g => g.SuiteHash, StringComparer.Ordinal).ThenByDescending(g => g.GradingVersion).ToList();
    }

    /// <summary>Model x category and model x complexity pass-rate matrices over the latest completed run per model.</summary>
    public static BreakdownResult Breakdown(BenchDb db, ExecutionMode mode, string suiteHash)
    {
        // Groups is filtered to one hash, so groups differ only by grading version: use the newest methodology.
        var rows = Groups(db, mode, suiteHash, false).MaxBy(g => g.GradingVersion)?.Rows.ToList() ?? [];
        Matrix Build(Func<RunSummary, IReadOnlyList<Slice>> pick)
        {
            var cols = rows.SelectMany(r => pick(r).Select(s => s.Key)).Distinct().Order(StringComparer.Ordinal).ToList();
            return new Matrix(rows.Select(r => r.ModelRef).ToList(), cols,
                rows.Select(r => cols.Select(c => pick(r).FirstOrDefault(s => s.Key == c) is { } s ? (double?)s.PassRate : null).ToArray()).ToArray());
        }
        return new BreakdownResult(Build(r => r.ByCategory), Build(r => r.ByComplexity));
    }

    /// <summary>Every non-passed attempt (failed, infra error, timeout, cancelled) with its failed assertions.</summary>
    public static List<FailedAttempt> Failures(BenchDb db, Guid runId)
        => db.Attempts.AsNoTracking().Include(a => a.Task).Include(a => a.Assertions)
            .Where(a => a.RunId == runId && a.Outcome != AttemptOutcome.Passed && a.Outcome != AttemptOutcome.Pending).ToList()
            .OrderBy(a => a.Task!.Index).ThenBy(a => a.RepeatIndex)
            .Select(a => new FailedAttempt(a.Id, a.Task!.Index, a.Task.FamilyId, a.Task.Complexity, a.RepeatIndex, a.Outcome, a.Score, a.ErrorMessage,
                a.Assertions.Where(x => !x.Passed).OrderBy(x => x.Id)
                    .Select(x => new FailedAssertion(x.Grader, x.Name, x.Expected, x.Actual, x.Diagnostic, x.Score, x.Weight, x.Required)).ToList()))
            .ToList();

    /// <summary>A task counts as passed when it has graded repeats and every one passed. Runs must share a suite content hash.</summary>
    public static Comparison Compare(BenchDb db, Guid runA, Guid runB)
    {
        var a = Summarize(db, runA);
        var b = Summarize(db, runB);
        if (a.SuiteHash != b.SuiteHash)
            throw new InvalidOperationException($"runs are not comparable: suite content hashes differ ({a.SuiteHash[..12]} vs {b.SuiteHash[..12]})");
        if (a.GradingVersion != b.GradingVersion)
            throw new InvalidOperationException($"runs are not comparable: graded with different methodologies (v{a.GradingVersion} vs v{b.GradingVersion})");
        var ra = AttemptRows(db, runA).Where(r => IsGraded(r.Outcome)).GroupBy(r => r.TaskIndex).ToDictionary(g => g.Key);
        var rb = AttemptRows(db, runB).Where(r => IsGraded(r.Outcome)).GroupBy(r => r.TaskIndex).ToDictionary(g => g.Key);
        var tasks = db.Tasks.AsNoTracking().Where(t => t.SuiteId == a.SuiteId).OrderBy(t => t.Index).Select(t => new { t.Index, t.FamilyId, t.Complexity }).ToList()
            .Select(t =>
            {
                ra.TryGetValue(t.Index, out var ga);
                rb.TryGetValue(t.Index, out var gb);
                var change = ga is null || gb is null ? TaskChange.Unscored
                    : (ga.All(r => r.Outcome == AttemptOutcome.Passed), gb.All(r => r.Outcome == AttemptOutcome.Passed)) switch
                    {
                        (true, true) => TaskChange.BothPass, (true, false) => TaskChange.PassToFail,
                        (false, true) => TaskChange.FailToPass, _ => TaskChange.BothFail,
                    };
                return new TaskDiff(t.Index, t.FamilyId, t.Complexity, change, ga?.Average(r => r.Score ?? 0), gb?.Average(r => r.Score ?? 0));
            }).ToList();
        List<MetricDelta> deltas =
        [
            new("cost_usd (total)", a.Metric(MetricNames.CostUsd).Total, b.Metric(MetricNames.CostUsd).Total),
            new("cost per success", a.CostPerSuccess, b.CostPerSuccess),
            new("input_tokens (total)", a.Metric(MetricNames.InputTokens).Total, b.Metric(MetricNames.InputTokens).Total),
            new("output_tokens (total)", a.Metric(MetricNames.OutputTokens).Total, b.Metric(MetricNames.OutputTokens).Total),
            new("ttft_ms (median)", a.Metric(MetricNames.TimeToFirstTokenMs).Median, b.Metric(MetricNames.TimeToFirstTokenMs).Median),
            new("throughput_tps (mean)", a.Metric(MetricNames.ThroughputTps).Mean, b.Metric(MetricNames.ThroughputTps).Mean),
            new("duration_ms (total)", a.Metric(MetricNames.DurationMs).Total, b.Metric(MetricNames.DurationMs).Total),
        ];
        return new Comparison(a, b, tasks, deltas);
    }

    /// <summary>Completed runs of a model on one suite content hash, oldest first.</summary>
    public static List<RunSummary> History(BenchDb db, string modelRef, string suiteHash)
        => db.Runs.AsNoTracking().Include(r => r.Suite).Where(r => r.ModelRef == modelRef && r.Status == RunStatus.Completed).ToList()
            .Where(r => r.Suite!.ContentHash == suiteHash).OrderBy(r => r.CreatedAt).Select(r => Summarize(db, r.Id)).ToList();
}
