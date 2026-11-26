using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Generation;
using ModelBench.Core.Grading;
using ModelBench.Core.Metrics;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Reporting;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Execution;

public class ConcurrencyAndResumeTests
{
    private static RunExecutor Exec(ResolvedModel m, TimeSpan? poll = null)
        => new(ProviderFactory.Create(m), m, new UnavailableSandbox()) { CancelPollInterval = poll ?? TimeSpan.FromSeconds(2) };

    private static Func<int, JsonObject, FakeReply> Slow(int ms) => (_, _) => { Thread.Sleep(ms); return FakeReply.Text("<answer>1</answer>"); };

    private static void AssertOneTerminalRowPerItem(BenchDb db, Guid runId, int tasks, int repeats)
    {
        var rows = db.Attempts.AsNoTracking().Where(a => a.RunId == runId).ToList();
        Assert.Equal(tasks * repeats, rows.Count);
        Assert.Equal(rows.Count, rows.Select(a => (a.TaskId, a.RepeatIndex)).Distinct().Count());
        Assert.All(rows, a => Assert.True(a.Outcome is AttemptOutcome.Passed or AttemptOutcome.Failed, a.Outcome.ToString()));
    }

    [Fact] public async Task FourWorkersRunConcurrentlyAndPersistEveryAttemptOnce()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        // Concurrency is proven by the peak number of in-flight requests seen by the server, not by wall time
        // (wall-time assertions flake when the full suite runs Docker tests in parallel).
        int inFlight = 0, peak = 0;
        await using var server = await FakeServer.StartAsync((_, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            Thread.Sleep(300);
            Interlocked.Decrement(ref inFlight);
            return FakeReply.Text("<answer>1</answer>");
        });
        var suite = TestKit.MakeSuite(db, 8);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null, concurrency: 4);
        var done = new List<int>();
        var ex = Exec(model);
        ex.AttemptCompleted += p => done.Add(p.Done);
        await ex.ExecuteAsync(db, run.Id, default);
        Assert.True(peak >= 2, $"peak in-flight requests {peak}: workers did not overlap");
        Assert.True(peak <= 4, $"peak in-flight requests {peak} exceeds concurrency 4");
        AssertOneTerminalRowPerItem(db, run.Id, 8, 1);
        Assert.Equal(Enumerable.Range(1, 8), done); // progress is serialised and monotonic
        Assert.Equal(RunStatus.Completed, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
    }

    [Fact] public async Task CancelThenResumeLeavesExactlyOneTerminalRowPerItem()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync(Slow(50));
        var suite = TestKit.MakeSuite(db, 6);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 2, 30, new RetryPolicy(), null, concurrency: 2);
        using var cts = new CancellationTokenSource();
        var ex = Exec(model);
        ex.AttemptCompleted += p => { if (p.Done == 3) cts.Cancel(); };
        await ex.ExecuteAsync(db, run.Id, cts.Token);
        Assert.Equal(RunStatus.Cancelled, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
        Assert.DoesNotContain(db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id).ToList(), a => a.Outcome == AttemptOutcome.Pending);

        var resumed = RunService.PrepareResume(db, run.Id, ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl)));
        await Exec(resumed).ExecuteAsync(db, run.Id, default);
        AssertOneTerminalRowPerItem(db, run.Id, 6, 2);
        Assert.Equal(RunStatus.Completed, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
    }

    [Fact] public async Task InFlightAttemptsAreRecordedCancelledNeverPending()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync(Slow(4000));
        var suite = TestKit.MakeSuite(db, 4);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null, concurrency: 2);
        using var cts = new CancellationTokenSource(1200);
        await Exec(model).ExecuteAsync(db, run.Id, cts.Token);
        var rows = db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id).ToList();
        Assert.InRange(rows.Count, 1, 2);
        Assert.All(rows, a => Assert.Equal(AttemptOutcome.Cancelled, a.Outcome));
    }

    [Fact] public async Task ResumeAfterSimulatedCrashRedoesPendingAndMissingAttempts()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>1</answer>"));
        var suite = TestKit.MakeSuite(db, 4);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        var tasks = db.Tasks.Where(t => t.SuiteId == suite.Id).OrderBy(t => t.Index).ToList();
        run.Status = RunStatus.Running; // the process died here
        db.Attempts.Add(new Attempt { RunId = run.Id, TaskId = tasks[0].Id, Outcome = AttemptOutcome.Passed, Score = 1 });
        db.Attempts.Add(new Attempt { RunId = run.Id, TaskId = tasks[1].Id, Outcome = AttemptOutcome.Pending });
        db.Attempts.Add(new Attempt { RunId = run.Id, TaskId = tasks[2].Id, Outcome = AttemptOutcome.Pending });
        db.SaveChanges();

        var m = RunService.PrepareResume(db, run.Id, ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl)));
        await Exec(m).ExecuteAsync(db, run.Id, default);
        Assert.Equal(3, server.Calls); // task 0 is kept
        Assert.Equal(AttemptOutcome.Passed, db.Attempts.Single(a => a.TaskId == tasks[0].Id).Outcome);
        Assert.Equal(4, db.Attempts.Count(a => a.RunId == run.Id));
        Assert.DoesNotContain(db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id).ToList(), a => a.Outcome == AttemptOutcome.Pending);
        Assert.Equal(RunStatus.Completed, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
    }

    [Fact] public async Task RetryInfraReplacesInfraErrorAndTimeoutRowsAndAccumulatesRetries()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var broken = true;
        await using var server = await FakeServer.StartAsync((_, _) => broken ? FakeReply.Error(500) : FakeReply.Text("<answer>1</answer>"));
        var suite = TestKit.MakeSuite(db, 2);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(1, 1, 5), null);
        await Exec(model).ExecuteAsync(db, run.Id, default);
        Assert.All(db.Attempts.Where(a => a.RunId == run.Id).ToList(), a => Assert.Equal((AttemptOutcome.InfraError, 1), (a.Outcome, a.Retries)));
        var timedOut = db.Attempts.First(a => a.RunId == run.Id);
        timedOut.Outcome = AttemptOutcome.Timeout;
        db.SaveChanges();

        broken = false;
        var calls = server.Calls;
        await Exec(model).ExecuteAsync(db, run.Id, default); // plain resume leaves infra rows alone
        Assert.Equal(calls, server.Calls);

        await Exec(model).ExecuteAsync(db, run.Id, default, retryInfra: true);
        Assert.Equal(calls + 2, server.Calls);
        var rows = db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, a => Assert.Equal((true, 1), (a.Outcome is AttemptOutcome.Passed or AttemptOutcome.Failed, a.Retries)));
    }

    [Fact] public async Task ChangedModelConfigurationIsAlwaysRefusedWithADiff()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        var suite = TestKit.MakeSuite(db, 1);
        var run = RunService.CreateRun(db, suite.Id, TestKit.Model(server.BaseUrl), ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), "mine");
        var changed = ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl).Replace("\"maxTokens\":512", "\"maxTokens\":999"));

        // MB-005: no override exists, so a run's attempts can never mix model configurations.
        var ex = Assert.Throws<InvalidOperationException>(() => RunService.PrepareResume(db, run.Id, changed));
        Assert.Contains("maxTokens: 512 -> 999", ex.Message);
        Assert.Contains("start a new run", ex.Message);
        Assert.Equal("mine", db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Label);
    }

    [Fact] public async Task ASecondExecutorIsRefusedWhileTheLeaseIsHeldAndNoInferenceIsDuplicated()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync(Slow(300));
        var suite = TestKit.MakeSuite(db, 4);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        var started = new TaskCompletionSource();
        var first = Exec(model, TimeSpan.FromMilliseconds(100));
        first.AttemptCompleted += _ => started.TrySetResult();
        var running = first.ExecuteAsync(db, run.Id, default);
        await started.Task;

        // Another process: its own connection, both entry points.
        using (var other = BenchDb.Open(db.DbPath))
        {
            await Assert.ThrowsAsync<RunLeaseException>(() => Exec(model).ExecuteAsync(other, run.Id, default));
            Assert.Throws<RunLeaseException>(() => RunService.PrepareResume(other, run.Id, ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl))));
        }
        await running;
        Assert.Equal(4, server.Calls);
        AssertOneTerminalRowPerItem(db, run.Id, 4, 1);
        var done = db.Runs.AsNoTracking().Single(r => r.Id == run.Id);
        Assert.Equal((RunStatus.Completed, (Guid?)null, (long?)null), (done.Status, done.LeaseOwner, done.LeaseExpiresMs)); // released
    }

    [Fact] public async Task AnExpiredLeaseFromACrashedExecutorCanBeTakenOver()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>1</answer>"));
        var suite = TestKit.MakeSuite(db, 2);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        run.Status = RunStatus.Running; // the owner died without releasing
        run.LeaseOwner = Guid.NewGuid();
        run.LeaseExpiresMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
        db.SaveChanges();

        var m = RunService.PrepareResume(db, run.Id, ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl)));
        await Exec(m).ExecuteAsync(db, run.Id, default);
        Assert.Equal(RunStatus.Completed, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
        AssertOneTerminalRowPerItem(db, run.Id, 2, 1);
    }

    [Fact] public async Task RetriedAttemptReportsUsageUnavailableAndCountsTheFailedTryInDuration()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var n = 0;
        // First request fails after 300 ms (retryable 500); the retry succeeds with known usage.
        await using var server = await FakeServer.StartAsync((_, _) => Interlocked.Increment(ref n) == 1 ? SlowError() : FakeReply.Text("<answer>1</answer>"));
        var suite = TestKit.MakeSuite(db, 1);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(1, 1, 5), null);
        await Exec(model).ExecuteAsync(db, run.Id, default);

        var a = db.Attempts.AsNoTracking().Include(x => x.Metrics).Single(x => x.RunId == run.Id);
        Assert.Equal(1, a.Retries);
        double? V(string name) => a.Metrics.Single(m => m.Name == name).Value;
        Assert.All(new[] { MetricNames.InputTokens, MetricNames.OutputTokens, MetricNames.ModelCalls, MetricNames.CostUsd },
            name => Assert.Equal(Provenance.Unavailable, a.Metrics.Single(m => m.Name == name).Provenance));
        Assert.True(V(MetricNames.DurationMs) >= 300, $"duration {V(MetricNames.DurationMs)} ms excludes the failed try");
        Assert.Null(Reports.Summarize(db, run.Id).CostPerSuccess);

        static FakeReply SlowError() { Thread.Sleep(300); return FakeReply.Error(500); }
    }

    [Fact] public async Task UnfinishedEpisodeReportsUsageUnavailableNotMeasuredZero()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Error(401));
        var suite = TestKit.MakeSuite(db, 1);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        await Exec(model).ExecuteAsync(db, run.Id, default);

        var a = db.Attempts.AsNoTracking().Include(x => x.Metrics).Single(x => x.RunId == run.Id);
        Assert.Equal(AttemptOutcome.InfraError, a.Outcome);
        Assert.Equal(Provenance.Unavailable, a.Metrics.Single(m => m.Name == MetricNames.ModelCalls).Provenance);
    }

    [Fact] public async Task ResumeIsRefusedForARunGradedWithAnotherMethodology()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        var suite = TestKit.MakeSuite(db, 1);
        var run = RunService.CreateRun(db, suite.Id, TestKit.Model(server.BaseUrl), ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        run.GradingVersion = 1;
        db.SaveChanges();

        var ex = Assert.Throws<InvalidOperationException>(() => RunService.PrepareResume(db, run.Id, ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl))));
        Assert.Contains("would mix them", ex.Message);

        // The executor itself refuses too (a caller that skips PrepareResume), before any inference or mutation.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Exec(TestKit.Model(server.BaseUrl)).ExecuteAsync(db, run.Id, default));
        Assert.Equal(0, server.Calls);
        var row = db.Runs.AsNoTracking().Single(r => r.Id == run.Id);
        Assert.Equal((RunStatus.Pending, (Guid?)null), (row.Status, row.LeaseOwner));
        Assert.Empty(db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id));
    }

    [Fact] public async Task AStaleOwnerCannotCommitItsResultOrStatusAndTheNewOwnerFinishes()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        using var release = new ManualResetEventSlim();
        using var inProvider = new ManualResetEventSlim();
        await using var server = await FakeServer.StartAsync((_, _) => { inProvider.Set(); release.Wait(); return FakeReply.Text("<answer>1</answer>"); });
        var suite = TestKit.MakeSuite(db, 1);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);

        // A heartbeats rarely, so it cannot notice the takeover itself: only the write fence can stop it.
        var aRun = Exec(model, TimeSpan.FromHours(1)).ExecuteAsync(db, run.Id, default);
        await Task.Run(() => inProvider.Wait());

        var b = Guid.NewGuid();
        using (var other = BenchDb.Open(db.DbPath)) // B took the expired lease and is now the owner
            other.Runs.Where(r => r.Id == run.Id).ExecuteUpdate(s => s.SetProperty(r => r.LeaseOwner, b).SetProperty(r => r.LeaseExpiresMs, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()));
        release.Set();

        await Assert.ThrowsAsync<RunLeaseException>(() => aRun);
        Assert.Empty(db.Attempts.AsNoTracking().Where(x => x.RunId == run.Id)); // A's stale result was not committed
        var row = db.Runs.AsNoTracking().Single(r => r.Id == run.Id);
        Assert.Equal((RunStatus.Running, (Guid?)b), (row.Status, row.LeaseOwner)); // nor its status, and A did not release B's lease

        // B's lease runs out and a fresh executor finishes the run.
        db.Runs.Where(r => r.Id == run.Id).ExecuteUpdate(s => s.SetProperty(r => r.LeaseExpiresMs, 1L));
        await Exec(model).ExecuteAsync(db, run.Id, default);
        AssertOneTerminalRowPerItem(db, run.Id, 1, 1);
        Assert.Equal(RunStatus.Completed, db.Runs.AsNoTracking().Single(r => r.Id == run.Id).Status);
    }

    private sealed class ToolThenFailProvider : IModelProvider
    {
        private int _n;
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
            => _n++ == 0 ? new ScriptedProvider([new ChatMessage(Role.Assistant, null, [new ToolCall("c1", "lookup", "{}")])]).CompleteAsync(request, ct)
                : throw new InfraException("boom", retryable: false);
    }

    [Fact] public async Task MultiCallEpisodeThatFailsBeforeReturningReportsUsageUnavailableNotMeasuredZero()
    {
        // PR #5 review: the first request of a tool loop succeeded, the second failed (no retries): no episode, but a call was made.
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = SuiteGenerator.Generate(new SuiteSpec("tools", 5, [new FamilySpec("tool-calling", 2, 1)]));
        SuiteGenerator.Persist(db, suite);
        var model = TestKit.Model("http://127.0.0.1:9/v1");
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.ToolAssisted, 1, 30, new RetryPolicy(), null);
        await new RunExecutor(new ToolThenFailProvider(), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);

        var a = db.Attempts.AsNoTracking().Include(x => x.Metrics).Single(x => x.RunId == run.Id);
        Assert.Equal((AttemptOutcome.InfraError, 0), (a.Outcome, a.Retries));
        Assert.Equal(Provenance.Unavailable, a.Metrics.Single(m => m.Name == MetricNames.ModelCalls).Provenance);
    }

    [Fact] public async Task ModifiedSuiteIsRefused()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("x"));
        var suite = TestKit.MakeSuite(db, 2);
        var run = RunService.CreateRun(db, suite.Id, TestKit.Model(server.BaseUrl), ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        var catalog = ModelCatalog.Parse(TestKit.ModelsJson(server.BaseUrl));
        RunService.PrepareResume(db, run.Id, catalog);
        db.Tasks.First(t => t.SuiteId == suite.Id).PromptJson += " ";
        db.SaveChanges();
        Assert.Contains("content hash", Assert.Throws<InvalidOperationException>(() => RunService.PrepareResume(db, run.Id, catalog)).Message);
    }

    [Fact] public async Task CancelFlagSetFromAnotherConnectionStopsTheExecutor()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync(Slow(400));
        var suite = TestKit.MakeSuite(db, 6);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null);
        var exec = Exec(model, TimeSpan.FromMilliseconds(50)).ExecuteAsync(db, run.Id, default);
        await Task.Delay(700);
        using (var other = dir.OpenDb()) RunService.RequestCancel(other, run.Id);
        await exec;
        var after = db.Runs.AsNoTracking().Single(r => r.Id == run.Id);
        Assert.Equal(RunStatus.Cancelled, after.Status);
        var rows = db.Attempts.AsNoTracking().Where(a => a.RunId == run.Id).ToList();
        Assert.InRange(rows.Count, 1, 5);
        Assert.DoesNotContain(rows, a => a.Outcome == AttemptOutcome.Pending);

        await Exec(model).ExecuteAsync(db, run.Id, default); // a cancelled run resumes, and the stale flag is cleared
        AssertOneTerminalRowPerItem(db, run.Id, 6, 1);
    }

    [Fact] public void RequestCancelRefusesFinishedRuns()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var run = RunService.CreateRun(db, TestKit.MakeSuite(db, 1).Id, TestKit.Model("http://127.0.0.1:9/v1"), ExecutionMode.SingleTurn, 1, 5, new RetryPolicy(), null);
        run.Status = RunStatus.Completed;
        db.SaveChanges();
        Assert.Throws<InvalidOperationException>(() => RunService.RequestCancel(db, run.Id));
    }
}

public class RetryTests
{
    [Fact] public void JitterIsDeterministicBoundedAndVariesPerAttemptIdentity()
    {
        var p = new RetryPolicy(3, 1000, 30000);
        Guid run = Guid.NewGuid(), task = Guid.NewGuid();
        Assert.Equal(p.Delay(2, run, task, 0), p.Delay(2, run, task, 0));
        Assert.InRange(p.Delay(2, run, task, 0).TotalMilliseconds, 2000, 4000);
        Assert.NotEqual(p.Delay(2, run, task, 0), p.Delay(2, run, task, 1));
        Assert.NotEqual(p.Delay(2, run, task, 0), p.Delay(1, run, task, 0) * 2);
    }

    [Fact] public void RetryAfterWinsWhenLongerButIsCappedAtMaxDelay()
    {
        var p = new RetryPolicy(3, 1000, 5000);
        Assert.Equal(TimeSpan.FromSeconds(3), p.Delay(0, Guid.Empty, Guid.Empty, 0, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(5), p.Delay(0, Guid.Empty, Guid.Empty, 0, TimeSpan.FromSeconds(60)));
    }

    [Fact] public async Task ServerRetryAfterIsHonoured()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((i, _) => i == 0 ? new FakeReply(429, null, "slow down", "1") : FakeReply.Text("<answer>1</answer>"));
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, TestKit.MakeSuite(db, 1).Id, model, ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(3, 1, 5000), null);
        var sw = Stopwatch.StartNew();
        await new RunExecutor(ProviderFactory.Create(model), model, new UnavailableSandbox()).ExecuteAsync(db, run.Id, default);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(950), $"waited only {sw.Elapsed}");
        Assert.Equal(1, db.Attempts.Single(a => a.RunId == run.Id).Retries);
        Assert.Equal(2, server.Calls);
    }
}

public class FailureClassificationTests
{
    private static async Task<(Attempt a, int calls, RunSummary s)> One(FakeReply reply, ISandbox sandbox, string family = "mathematics", int timeout = 30)
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => reply);
        var suite = SuiteGenerator.Generate(new SuiteSpec("c", 7, [new FamilySpec(family, 1, 1)]));
        SuiteGenerator.Persist(db, suite);
        var model = TestKit.Model(server.BaseUrl);
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, timeout, new RetryPolicy(3, 1, 5), null);
        await new RunExecutor(ProviderFactory.Create(model), model, sandbox).ExecuteAsync(db, run.Id, default);
        return (db.Attempts.AsNoTracking().Single(a => a.RunId == run.Id), server.Calls, Reports.Summarize(db, run.Id));
    }

    [Fact] public async Task EmptyResponseIsAModelFailureNotRetried()
    {
        var (a, calls, s) = await One(FakeReply.Text(""), new UnavailableSandbox());
        Assert.Equal((AttemptOutcome.Failed, 0, 1), (a.Outcome, a.Retries, calls));
        Assert.Equal(1, s.Graded);
    }

    [Fact] public async Task MalformedAnswerIsAModelFailureNotRetried()
    {
        var (a, calls, _) = await One(FakeReply.Text("<answer>unclosed"), new UnavailableSandbox());
        Assert.Equal((AttemptOutcome.Failed, 0, 1), (a.Outcome, a.Retries, calls));
    }

    [Fact] public async Task SandboxInfraFailureWhileGradingIsInfraError()
    {
        var (a, _, s) = await One(FakeReply.Text("```python\nprint(1)\n```"), new UnavailableSandbox(), "code-generation");
        Assert.Equal(AttemptOutcome.InfraError, a.Outcome);
        Assert.Null(a.Score);
        Assert.Equal((1, 0), (s.InfraErrors, s.Graded));
    }

    [Fact] public async Task TimeoutIsExcludedFromScores()
    {
        var slow = new FakeReply(200, Enumerable.Repeat(FakeReply.Delta(new JsonObject { ["content"] = "." }), 400).ToList());
        var (a, _, s) = await One(slow, new UnavailableSandbox(), timeout: 1);
        Assert.Equal(AttemptOutcome.Timeout, a.Outcome);
        Assert.Equal((1, 1, 0, null), (s.Timeouts, s.Excluded, s.Graded, s.PassRate));
    }
}

public class MigrationTests
{
    [Fact] public void FreshDatabaseIsMigratedAndRecordsHistory()
    {
        using var dir = new TempDir();
        using (var db = dir.OpenDb()) Assert.NotEmpty(db.Database.GetAppliedMigrations());
        using var again = dir.OpenDb(); // reopening an up-to-date database is a no-op
        Assert.Empty(again.Database.GetPendingMigrations());
    }

    [Fact] public void EnsureCreatedDatabaseIsRejectedWithAClearMessage()
    {
        using var dir = new TempDir();
        using (var legacy = new BenchDb(dir.File("test.db"))) legacy.Database.EnsureCreated();
        var ex = Assert.Throws<InvalidOperationException>(() => dir.OpenDb());
        Assert.Contains("delete or rename", ex.Message);
    }
}

public class RunCliTests
{
    [Fact] public async Task ResumeAndCancelCommands()
    {
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        await using var server = await FakeServer.StartAsync((_, _) => FakeReply.Text("<answer>1</answer>"));
        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson(server.BaseUrl));
        var run = RunService.CreateRun(db, TestKit.MakeSuite(db, 3).Id, TestKit.Model(server.BaseUrl), ExecutionMode.SingleTurn, 1, 30, new RetryPolicy(), null, concurrency: 2);
        string[] common = ["--db", dir.File("test.db"), "--models", dir.File("models.json"), "run"];

        using var o1 = new StringWriter();
        Assert.Equal(0, await ModelBench.Cli.CliApp.RunAsync([.. common, "cancel", run.Id.ToString()], o1, o1));
        Assert.True(db.Runs.AsNoTracking().Single(r => r.Id == run.Id).CancelRequested);

        using var o2 = new StringWriter();
        Assert.Equal(0, await ModelBench.Cli.CliApp.RunAsync([.. common, "resume", run.Id.ToString()], o2, o2));
        Assert.Contains("[3/3]", o2.ToString());
        Assert.Equal((RunStatus.Completed, false), db.Runs.AsNoTracking().Select(r => new { r.Status, r.CancelRequested }).AsEnumerable().Select(r => (r.Status, r.CancelRequested)).Single());
        Assert.Equal(3, db.Attempts.Count(a => a.RunId == run.Id));

        using var o3 = new StringWriter();
        Assert.Equal(1, await ModelBench.Cli.CliApp.RunAsync([.. common, "cancel", run.Id.ToString()], o3, o3)); // finished: nothing to cancel
        Assert.Contains("nothing to cancel", o3.ToString());

        File.WriteAllText(dir.File("models.json"), TestKit.ModelsJson(server.BaseUrl).Replace("\"maxTokens\":512", "\"maxTokens\":9"));
        using var o4 = new StringWriter();
        Assert.Equal(1, await ModelBench.Cli.CliApp.RunAsync([.. common, "resume", run.Id.ToString()], o4, o4));
        Assert.Contains("start a new run", o4.ToString());
    }
}
