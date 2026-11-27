using ModelBench.Core.Domain;
using ModelBench.Core.Reporting;
using ModelBench.Tests.Support;
using static ModelBench.Core.Domain.AttemptOutcome;

namespace ModelBench.Tests.Reporting;

public class ReportingTests
{
    private const ExecutionMode ST = ExecutionMode.SingleTurn;

    [Fact] public void PassRateExcludesInfraTimeoutAndCancelled()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var run = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed), new A(1, 0, Failed), new A(2, 0, InfraError), new A(3, 0, AttemptOutcome.Timeout), new A(0, 1, Cancelled));
        var s = Reports.Summarize(db, run.Id);
        Assert.Equal((5, 2, 1), (s.Attempts, s.Graded, s.Passed));
        Assert.Equal(0.5, s.PassRate);
        Assert.Equal(0.5, s.MeanScore);
        Assert.Equal((1, 1, 1, 3), (s.InfraErrors, s.Timeouts, s.Cancelled, s.Excluded));
        Assert.Equal(["math"], s.ByCategory.Select(c => c.Key).ToArray());
    }

    private static A[] Seq(params AttemptOutcome[] outcomes) => [.. outcomes.Select((o, i) => new A(i % 4, i / 4, o))];

    [Fact] public void TimeoutsCountAgainstEndToEndRankingAndInfraErrorsShowAsIncompletion()
    {
        // MB-001: 1 pass + 9 timeouts used to rank at 100% (graded only), above 9 passes + 1 fail at 90%.
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var timeouts = ReportSeed.AddRun(db, suite, "m/times-out", ST, ReportSeed.T0, Seq([Passed, .. Enumerable.Repeat(AttemptOutcome.Timeout, 11)]));
        var solid = ReportSeed.AddRun(db, suite, "m/solid", ST, ReportSeed.T0, Seq([.. Enumerable.Repeat(Passed, 11), Failed]));
        var flaky = ReportSeed.AddRun(db, suite, "m/infra", ST, ReportSeed.T0, Seq(Passed, Passed, InfraError, Cancelled));

        var t = Reports.Summarize(db, timeouts.Id);
        Assert.Equal((1.0, 1.0 / 12, 1.0), (t.PassRate, t.EndToEndRate, t.Completion));
        var s = Reports.Summarize(db, solid.Id);
        Assert.Equal((11.0 / 12, 11.0 / 12, 1.0), (s.PassRate, s.EndToEndRate, s.Completion));
        var f = Reports.Summarize(db, flaky.Id);
        Assert.Equal((1.0, 1.0, 0.5), (f.PassRate, f.EndToEndRate, f.Completion)); // infra/cancel are not the model's fault, but are disclosed

        // Complete runs rank first: m/infra has the best rate but only half of its attempts are accountable.
        Assert.Equal(["m/solid", "m/times-out", "m/infra"], Reports.Leaderboard(db, ST).Single().Rows.Select(r => r.ModelRef).ToArray());
    }

    [Fact] public void IncompleteCoverageIsMarkedAndNeverRanksAboveACompleteRun()
    {
        // PR #5 review: 1 pass + 9 infra errors used to rank at 100% ahead of 9 passes + 1 fail; an interrupted run with one stored row looked complete.
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var infra = ReportSeed.AddRun(db, suite, "m/infra", ST, ReportSeed.T0, Seq([Passed, .. Enumerable.Repeat(InfraError, 7)]));
        var solid = ReportSeed.AddRun(db, suite, "m/solid", ST, ReportSeed.T0, Seq([.. Enumerable.Repeat(Passed, 7), Failed]));
        var interrupted = ReportSeed.AddRun(db, suite, "m/interrupted", ST, ReportSeed.T0, new A(0, 0, Passed));
        interrupted.Repeats = 2; // planned: 4 tasks x 2 repeats, but only one result was stored
        db.SaveChanges();

        var i = Reports.Summarize(db, interrupted.Id);
        Assert.Equal((1.0, 8, 0.125, false), (i.EndToEndRate, i.Planned, i.Completion, i.Authoritative));
        Assert.False(Reports.Summarize(db, infra.Id).Authoritative);
        Assert.True(Reports.Summarize(db, solid.Id).Authoritative);
        var rows = Reports.Leaderboard(db, ST).Single().Rows;
        Assert.Equal("m/solid", rows[0].ModelRef);
        Assert.Equal([false, false], rows.Skip(1).Select(r => r.Authoritative).ToArray()); // listed, after the ranked run
    }

    [Fact] public void DefaultLeaderboardKeepsTheLatestAuthoritativeRunOverANewerIncompleteOne()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var complete = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0, Seq(Passed, Failed, Passed, Failed));
        var incomplete = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0.AddDays(1), Seq(Passed, InfraError, InfraError, InfraError));
        Assert.True(Reports.Summarize(db, complete.Id).Authoritative);
        Assert.False(Reports.Summarize(db, incomplete.Id).Authoritative);

        Assert.Equal(complete.Id, Reports.Leaderboard(db, ST).Single().Rows.Single().RunId);
        Assert.Equal(2, Reports.Leaderboard(db, ST, allRuns: true).Single().Rows.Count);
    }

    [Fact] public void RunsGradedWithDifferentMethodologiesAreNeverRankedOrComparedTogether()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var oldRun = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed));
        var newRun = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0.AddDays(1), new A(0, 0, Failed));
        oldRun.GradingVersion = 1;
        db.SaveChanges();

        var board = Reports.Leaderboard(db, ST);
        Assert.Equal([Core.Grading.GradingPipeline.Version, 1], board.Select(g => g.GradingVersion).ToArray());
        Assert.All(board, g => Assert.Single(g.Rows));
        Assert.Contains("different methodologies", Assert.Throws<InvalidOperationException>(() => Reports.Compare(db, oldRun.Id, newRun.Id)).Message);
    }

    [Fact] public void CliCostCellShowsCoverage()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var run = ReportSeed.AddRun(db, ReportSeed.AddSuite(db), "m/a", ST, ReportSeed.T0, new A(0, 0, Passed, Cost: 1), new A(1, 0, Failed));
        Assert.Equal("1 (1/2)", ModelBench.Cli.Commands.ReportCommands.CostCell(Reports.Summarize(db, run.Id)));
    }

    [Fact] public void BreakdownIsKeyedByHashAndGradingVersion()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var oldRun = ReportSeed.AddRun(db, suite, "m/old", ST, ReportSeed.T0, new A(0, 0, Passed));
        ReportSeed.AddRun(db, suite, "m/new", ST, ReportSeed.T0, new A(0, 0, Failed));
        oldRun.GradingVersion = 1;
        db.SaveChanges();

        Assert.Equal(["m/old"], Reports.Breakdown(db, ST, suite.ContentHash, 1).ByCategory.Models);
        Assert.Equal(["m/new"], Reports.Breakdown(db, ST, suite.ContentHash, Core.Grading.GradingPipeline.Version).ByCategory.Models);
    }

    [Fact] public void NoGradedAttemptsMeansNoPassRate()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var run = ReportSeed.AddRun(db, ReportSeed.AddSuite(db), "m/a", ST, ReportSeed.T0, new A(0, 0, InfraError));
        Assert.Null(Reports.Summarize(db, run.Id).PassRate);
    }

    [Fact] public void ConsistencyCountsTasksWithRepeatsOnly()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var run = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0,
            new A(0, 0, Passed), new A(0, 1, Passed), new A(1, 0, Passed), new A(1, 1, Failed), new A(2, 0, Failed));
        Assert.Equal(0.5, Reports.Summarize(db, run.Id).Consistency);
        var single = ReportSeed.AddRun(db, suite, "m/b", ST, ReportSeed.T0, new A(0, 0, Passed));
        Assert.Null(Reports.Summarize(db, single.Id).Consistency);
    }

    [Fact] public void MetricAggregatesSkipUnavailableAndShowCoverage()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var run = ReportSeed.AddRun(db, ReportSeed.AddSuite(db), "m/a", ST, ReportSeed.T0,
            new A(0, 0, Passed, Cost: 1, Ttft: 100), new A(1, 0, Passed, Cost: null, Ttft: null), new A(2, 0, Failed, Cost: 3, Ttft: 300));
        var s = Reports.Summarize(db, run.Id);
        var cost = s.Metric(MetricNames.CostUsd);
        Assert.Equal((4.0, 2.0, "2/3"), (cost.Total, cost.Mean, cost.Coverage));
        Assert.Equal(200, s.Metric(MetricNames.TimeToFirstTokenMs).Median);
        Assert.Null(s.Metric(MetricNames.InputTokens).Total); // never recorded: unavailable, not 0
    }

    [Fact] public void CostPerSuccessIsNullWhenCostCoverageIsIncomplete()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = ReportSeed.AddSuite(db);
        var partial = ReportSeed.AddRun(db, suite, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed, Cost: 1), new A(1, 0, Passed, Cost: null));
        Assert.Null(Reports.Summarize(db, partial.Id).CostPerSuccess);
        var full = ReportSeed.AddRun(db, suite, "m/b", ST, ReportSeed.T0, new A(0, 0, Passed, Cost: 1), new A(1, 0, Passed, Cost: 2), new A(2, 0, Failed, Cost: 3));
        Assert.Equal(3.0, Reports.Summarize(db, full.Id).CostPerSuccess); // 6 / 2 passes
        var none = ReportSeed.AddRun(db, suite, "m/c", ST, ReportSeed.T0, new A(0, 0, Failed, Cost: 1));
        Assert.Null(Reports.Summarize(db, none.Id).CostPerSuccess);
    }

    [Fact] public void LeaderboardIsSeparatedByModeAndGroupedByHashWithLatestRunPerModel()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var s1 = ReportSeed.AddSuite(db, "one", "1111");
        var s2 = ReportSeed.AddSuite(db, "two", "2222");
        ReportSeed.AddRun(db, s1, "m/a", ST, ReportSeed.T0, new A(0, 0, Failed));
        var latest = ReportSeed.AddRun(db, s1, "m/a", ST, ReportSeed.T0.AddDays(1), new A(0, 0, Passed));
        ReportSeed.AddRun(db, s1, "m/b", ST, ReportSeed.T0, new A(0, 0, Passed), new A(1, 0, Failed));
        ReportSeed.AddRun(db, s1, "m/a", ExecutionMode.ToolAssisted, ReportSeed.T0, new A(0, 0, Failed));
        ReportSeed.AddRun(db, s2, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed));

        var board = Reports.Leaderboard(db, ST);
        Assert.Equal(["1111".PadRight(64, '0'), "2222".PadRight(64, '0')], board.Select(g => g.SuiteHash).ToArray());
        Assert.Equal(["m/a", "m/b"], board[0].Rows.Select(r => r.ModelRef).ToArray()); // sorted by pass rate
        Assert.Equal(latest.Id, board[0].Rows[0].RunId);
        Assert.Equal(4, Reports.Leaderboard(db, ST, allRuns: true).Sum(g => g.Rows.Count));
        Assert.All(Reports.Leaderboard(db, ExecutionMode.ToolAssisted).SelectMany(g => g.Rows), r => Assert.Equal(ExecutionMode.ToolAssisted, r.Mode));
        Assert.Single(Reports.Leaderboard(db, ST, s2.Id));
    }

    [Fact] public void BreakdownBuildsModelByCategoryAndComplexityMatrices()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var s = ReportSeed.AddSuite(db);
        ReportSeed.AddRun(db, s, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed), new A(1, 0, Failed), new A(2, 0, Passed));
        var b = Reports.Breakdown(db, ST, s.ContentHash, Core.Grading.GradingPipeline.Version);
        Assert.Equal(["code", "math"], b.ByCategory.Columns.ToArray());
        Assert.Equal([1.0, 0.5], b.ByCategory.Cells[0]);
        Assert.Equal(["1", "2"], b.ByComplexity.Columns.ToArray());
    }

    [Fact] public void CompareRefusesDifferentSuiteHashes()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var a = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "x", "1111"), "m/a", ST, ReportSeed.T0, new A(0, 0, Passed));
        var b = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "y", "2222"), "m/a", ST, ReportSeed.T0, new A(0, 0, Passed));
        var ex = Assert.Throws<InvalidOperationException>(() => Reports.Compare(db, a.Id, b.Id));
        Assert.Contains("not comparable", ex.Message);
    }

    [Fact] public void CompareClassifiesTransitionsAcrossSuitesWithEqualHash()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var a = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "x", "1111"), "m/a", ST, ReportSeed.T0, new A(0, 0, Passed), new A(1, 0, Passed), new A(2, 0, Failed), new A(3, 0, Failed));
        var b = ReportSeed.AddRun(db, ReportSeed.AddSuite(db, "x-copy", "1111"), "m/b", ST, ReportSeed.T0, new A(0, 0, Failed), new A(1, 0, Passed), new A(2, 0, Passed), new A(3, 0, Failed));
        var c = Reports.Compare(db, a.Id, b.Id);
        Assert.Equal([TaskChange.PassToFail, TaskChange.BothPass, TaskChange.FailToPass, TaskChange.BothFail], c.Tasks.Select(t => t.Change).ToArray());
        Assert.Equal(-1, c.Tasks[0].ScoreDelta);
        Assert.Equal(0, c.PassRateDelta);
    }

    [Fact] public void ComparePassesOnlyWhenEveryRepeatPasses()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var s = ReportSeed.AddSuite(db);
        var a = ReportSeed.AddRun(db, s, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed), new A(0, 1, Passed));
        var b = ReportSeed.AddRun(db, s, "m/a", ST, ReportSeed.T0, new A(0, 0, Passed), new A(0, 1, Failed), new A(1, 0, Passed));
        var c = Reports.Compare(db, a.Id, b.Id);
        Assert.Equal(TaskChange.PassToFail, c.Tasks[0].Change);
        Assert.Equal(TaskChange.Unscored, c.Tasks[1].Change);
    }

    [Fact] public void HistoryIsOldestFirstForOneModelAndHash()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var s = ReportSeed.AddSuite(db);
        var late = ReportSeed.AddRun(db, s, "m/a", ST, ReportSeed.T0.AddDays(2), new A(0, 0, Passed));
        var early = ReportSeed.AddRun(db, s, "m/a", ST, ReportSeed.T0, new A(0, 0, Failed));
        ReportSeed.AddRun(db, s, "m/other", ST, ReportSeed.T0, new A(0, 0, Passed));
        var h = Reports.History(db, "m/a", s.ContentHash);
        Assert.Equal([early.Id, late.Id], h.Select(r => r.RunId).ToArray());
        Assert.Equal([0.0, 1.0], h.Select(r => r.PassRate!.Value).ToArray());
    }

    [Fact] public void FailuresListFailedAssertions()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var run = ReportSeed.AddRun(db, ReportSeed.AddSuite(db), "m/a", ST, ReportSeed.T0, new A(0, 0, Passed), new A(1, 0, Failed), new A(2, 0, InfraError));
        var f = Reports.Failures(db, run.Id);
        Assert.Equal(2, f.Count);
        var x = Assert.Single(f[0].Assertions);
        Assert.Equal(("exact", "value", "1", "2", "wrong value", true), (x.Grader, x.Name, x.Expected, x.Actual, x.Diagnostic, x.Required));
        Assert.Equal("HTTP 500", f[1].Error);
    }
}
