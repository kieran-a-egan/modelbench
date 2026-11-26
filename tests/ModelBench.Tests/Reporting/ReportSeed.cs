using ModelBench.Core.Domain;
using ModelBench.Core.Persistence;

namespace ModelBench.Tests.Reporting;

/// <summary>One seeded attempt: task index, repeat, outcome, and optional cost/ttft (null = Unavailable).</summary>
public sealed record A(int Task, int Repeat, AttemptOutcome Outcome, double? Cost = null, double? Ttft = null, string? FinalText = null);

/// <summary>Hand-built Suites/Runs/Attempts/Assertions/Metrics. Tasks 0,1 are category "math" complexity 1; tasks 2,3 are "code" complexity 2.</summary>
public static class ReportSeed
{
    public static Suite AddSuite(BenchDb db, string name = "s", string hash = "aaaaaaaaaaaa")
    {
        var suite = new Suite { Name = name, SpecJson = "{}", ContentHash = hash.PadRight(64, '0'), MasterSeed = 1 };
        for (var i = 0; i < 4; i++)
            suite.Tasks.Add(new TaskInstance
            {
                SuiteId = suite.Id, Index = i, FamilyId = i < 2 ? "mathematics" : "code-generation", Category = i < 2 ? "math" : "code", Complexity = i < 2 ? 1 : 2,
                Mode = ExecutionMode.SingleTurn, PromptJson = JsonUtil.Serialize(TaskPrompt.Simple("question " + i)),
                HiddenJson = JsonUtil.Serialize(new TaskHidden([])), ContentHash = $"task{i}".PadRight(64, '0'),
            });
        db.Suites.Add(suite);
        db.SaveChanges();
        return suite;
    }

    public static Run AddRun(BenchDb db, Suite suite, string model, ExecutionMode mode, DateTimeOffset created, params A[] attempts)
    {
        var run = new Run
        {
            SuiteId = suite.Id, ModelRef = model, ModelSnapshotJson = "{}", Mode = mode, RetryPolicyJson = "{}", EnvironmentJson = "{}",
            Status = RunStatus.Completed, CreatedAt = created, CompletedAt = created.AddMinutes(1), Repeats = Math.Max(1, (attempts.Length + 3) / 4), // by default the seeded attempts are the whole plan (4 tasks x repeats)
        };
        db.Runs.Add(run);
        foreach (var a in attempts)
        {
            var graded = a.Outcome is AttemptOutcome.Passed or AttemptOutcome.Failed;
            var attempt = new Attempt
            {
                RunId = run.Id, TaskId = suite.Tasks.Single(t => t.Index == a.Task).Id, RepeatIndex = a.Repeat, Outcome = a.Outcome,
                Score = graded ? (a.Outcome == AttemptOutcome.Passed ? 1 : 0) : null, FinalText = a.FinalText ?? "answer",
                RawRequestsJson = "[{\"model\":\"m\"}]", RawResponsesJson = "[{\"id\":\"r\"}]", TranscriptJson = "[]",
                ErrorMessage = a.Outcome == AttemptOutcome.InfraError ? "HTTP 500" : null,
            };
            if (graded)
                attempt.Assertions.Add(new AssertionResult
                {
                    Grader = "exact", Name = "value", Expected = "1", Actual = a.Outcome == AttemptOutcome.Passed ? "1" : "2", Score = attempt.Score!.Value,
                    Required = true, Passed = a.Outcome == AttemptOutcome.Passed, Diagnostic = a.Outcome == AttemptOutcome.Passed ? null : "wrong value",
                });
            attempt.Metrics.Add(new Metric
            {
                Name = MetricNames.CostUsd, Unit = "usd", Value = a.Cost, Provenance = a.Cost is null ? Provenance.Unavailable : Provenance.Calculated,
            });
            attempt.Metrics.Add(new Metric
            {
                Name = MetricNames.TimeToFirstTokenMs, Unit = "ms", Value = a.Ttft, Provenance = a.Ttft is null ? Provenance.Unavailable : Provenance.Measured,
            });
            run.Attempts.Add(attempt);
        }
        db.SaveChanges();
        return run;
    }

    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
}
