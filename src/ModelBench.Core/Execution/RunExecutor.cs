using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Families;
using ModelBench.Core.Grading;
using ModelBench.Core.Metrics;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Execution;


public sealed record AttemptProgress(int Done, int Total, Guid TaskId, string FamilyId, int Complexity, int Repeat, AttemptOutcome Outcome, double? Score);

/// <summary>
/// Executes a run's (task x repeat) work items with Run.Concurrency workers, persisting each attempt as it finishes.
/// Work is queued in (task index, repeat) order; completion order is not deterministic when Concurrency > 1.
/// </summary>
public sealed class RunExecutor(IModelProvider provider, ResolvedModel model, ISandbox sandbox)
{
    private readonly object _progressLock = new();

    public event Action<AttemptProgress>? AttemptCompleted;

    /// <summary>How often the DB's Run.CancelRequested flag is polled (set by 'run cancel' from another process).</summary>
    public TimeSpan CancelPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs every missing, Pending or Cancelled attempt (so it is also the resume path). With <paramref name="retryInfra"/>,
    /// InfraError and Timeout attempts are replaced too; their Retries count is added to the new attempt's Retries.
    /// </summary>
    public async Task ExecuteAsync(BenchDb db, Guid runId, CancellationToken ct, bool retryInfra = false)
    {
        var run = await db.Runs.SingleAsync(r => r.Id == runId, CancellationToken.None);
        await db.Entry(run).ReloadAsync(CancellationToken.None); // the context may hold a stale tracked copy (e.g. from CreateRun)
        if (run.Mode == ExecutionMode.Manual) throw new InvalidOperationException("manual runs are completed with 'manual import'");
        var policy = JsonUtil.Deserialize<RetryPolicy>(run.RetryPolicyJson);

        var tasks = await db.Tasks.Where(t => t.SuiteId == run.SuiteId && t.Mode == run.Mode).OrderBy(t => t.Index).ToListAsync(CancellationToken.None);
        // Pending/Cancelled (and with retryInfra, InfraError/Timeout) attempts are redone; everything else is terminal and skipped.
        var redo = retryInfra
            ? new[] { AttemptOutcome.Pending, AttemptOutcome.Cancelled, AttemptOutcome.InfraError, AttemptOutcome.Timeout }
            : [AttemptOutcome.Pending, AttemptOutcome.Cancelled];
        var prior = (await db.Attempts.Where(a => a.RunId == runId && redo.Contains(a.Outcome)).Select(a => new { a.TaskId, a.RepeatIndex, a.Retries }).ToListAsync(CancellationToken.None))
            .ToDictionary(a => (a.TaskId, a.RepeatIndex), a => a.Retries);
        await db.Attempts.Where(a => a.RunId == runId && redo.Contains(a.Outcome)).ExecuteDeleteAsync(CancellationToken.None);
        var finished = (await db.Attempts.Where(a => a.RunId == runId).Select(a => new { a.TaskId, a.RepeatIndex }).ToListAsync(CancellationToken.None))
            .Select(a => (a.TaskId, a.RepeatIndex)).ToHashSet();

        run.Status = RunStatus.Running;
        run.CancelRequested = false;
        run.CompletedAt = null;
        await db.SaveChangesAsync(CancellationToken.None);

        var queue = Channel.CreateUnbounded<(TaskInstance Task, int Repeat)>();
        foreach (var task in tasks)
            for (var repeat = 0; repeat < run.Repeats; repeat++)
                if (!finished.Contains((task.Id, repeat))) queue.Writer.TryWrite((task, repeat));
        queue.Writer.Complete();

        var total = tasks.Count * run.Repeats;
        var done = finished.Count;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var pollStop = new CancellationTokenSource();
        var poller = PollCancelAsync(db, runId, work, pollStop.Token);

        async Task Worker()
        {
            try
            {
                using var wdb = BenchDb.Open(db.DbPath); // DbContext is not thread-safe: one per worker
                await foreach (var (task, repeat) in queue.Reader.ReadAllAsync(CancellationToken.None))
                {
                    if (work.IsCancellationRequested) break;
                    var attempt = await RunAttemptAsync(run, task, repeat, policy, work.Token);
                    attempt.Retries += prior.GetValueOrDefault((task.Id, repeat));
                    wdb.Attempts.Add(attempt);
                    await wdb.SaveChangesAsync(CancellationToken.None);
                    lock (_progressLock)
                        AttemptCompleted?.Invoke(new AttemptProgress(++done, total, task.Id, task.FamilyId, task.Complexity, repeat, attempt.Outcome, attempt.Score));
                }
            }
            catch
            {
                work.Cancel(); // stop the other workers; the run is marked Failed below
                throw;
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, run.Concurrency)).Select(_ => Task.Run(Worker, CancellationToken.None)));
            run.Status = work.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Completed;
        }
        catch
        {
            run.Status = RunStatus.Failed;
            throw;
        }
        finally
        {
            await pollStop.CancelAsync();
            await poller; // the poller shares db, so it must be finished before db is used again
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task PollCancelAsync(BenchDb db, Guid runId, CancellationTokenSource work, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(CancelPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                if (await db.Runs.AsNoTracking().Where(r => r.Id == runId).Select(r => r.CancelRequested).SingleAsync(CancellationToken.None))
                {
                    await work.CancelAsync();
                    return;
                }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<Attempt> RunAttemptAsync(Run run, TaskInstance task, int repeat, RetryPolicy policy, CancellationToken ct)
    {
        var attempt = new Attempt { RunId = run.Id, TaskId = task.Id, RepeatIndex = repeat, StartedAt = DateTimeOffset.UtcNow };
        var prompt = JsonUtil.Deserialize<TaskPrompt>(task.PromptJson);
        var hidden = JsonUtil.Deserialize<TaskHidden>(task.HiddenJson);
        EpisodeResult? episode = null;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(run.TimeoutSeconds));
        try
        {
            while (true)
            {
                try { episode = await EpisodeRunner.RunAsync(run.Mode, prompt, hidden, FamilyRegistry.Get(task.FamilyId), provider, cts.Token); break; }
                catch (InfraException ex) when (ex.Retryable && attempt.Retries < policy.MaxRetries)
                {
                    await Task.Delay(policy.Delay(attempt.Retries++, run.Id, task.Id, repeat, (ex as RetryAfterException)?.RetryAfter), cts.Token);
                }
            }
            var graded = await GradingPipeline.GradeAsync(prompt, hidden, episode.Transcript, episode.FinalEnvironmentState, sandbox, cts.Token);
            graded.ApplyTo(attempt);
            attempt.TranscriptJson = JsonUtil.Serialize(episode.Transcript);
            attempt.RawRequestsJson = JsonUtil.Serialize(episode.Responses.Select(r => r.RawRequest));
            attempt.RawResponsesJson = JsonUtil.Serialize(episode.Responses.Select(r => r.RawResponse));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            attempt.Outcome = AttemptOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            attempt.Outcome = AttemptOutcome.Timeout;
            attempt.ErrorMessage = $"attempt exceeded {run.TimeoutSeconds}s";
        }
        catch (InfraException ex)
        {
            attempt.Outcome = AttemptOutcome.InfraError;
            attempt.ErrorMessage = ex.Message;
        }
        attempt.CompletedAt = DateTimeOffset.UtcNow;
        attempt.Metrics.AddRange(MetricCalculator.Compute(episode?.Responses ?? [], episode?.WallTime ?? sw.Elapsed, model.Pricing, model.Energy));
        return attempt;
    }
}
