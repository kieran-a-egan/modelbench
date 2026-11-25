using System.Diagnostics;
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

/// <summary>Executes a run's (task x repeat) work items sequentially, persisting each attempt as it finishes.</summary>
public sealed class RunExecutor(IModelProvider provider, ResolvedModel model, ISandbox sandbox)
{
    public event Action<AttemptProgress>? AttemptCompleted;

    public async Task ExecuteAsync(BenchDb db, Guid runId, CancellationToken ct)
    {
        var run = await db.Runs.SingleAsync(r => r.Id == runId, CancellationToken.None);
        if (run.Mode == ExecutionMode.Manual) throw new InvalidOperationException("manual runs are completed with 'manual import'");
        var policy = JsonUtil.Deserialize<RetryPolicy>(run.RetryPolicyJson);

        var tasks = await db.Tasks.Where(t => t.SuiteId == run.SuiteId && t.Mode == run.Mode).OrderBy(t => t.Index).ToListAsync(CancellationToken.None);
        // Pending/Cancelled attempts are redone; everything else is terminal and skipped.
        await db.Attempts.Where(a => a.RunId == runId && (a.Outcome == AttemptOutcome.Pending || a.Outcome == AttemptOutcome.Cancelled)).ExecuteDeleteAsync(CancellationToken.None);
        var finished = (await db.Attempts.Where(a => a.RunId == runId).Select(a => new { a.TaskId, a.RepeatIndex }).ToListAsync(CancellationToken.None))
            .Select(a => (a.TaskId, a.RepeatIndex)).ToHashSet();

        run.Status = RunStatus.Running;
        await db.SaveChangesAsync(CancellationToken.None);
        var total = tasks.Count * run.Repeats;
        var done = finished.Count;
        try
        {
            foreach (var task in tasks)
                for (var repeat = 0; repeat < run.Repeats; repeat++)
                {
                    if (ct.IsCancellationRequested) break;
                    if (finished.Contains((task.Id, repeat))) continue;
                    var attempt = await RunAttemptAsync(run, task, repeat, policy, ct);
                    db.Attempts.Add(attempt);
                    await db.SaveChangesAsync(CancellationToken.None);
                    AttemptCompleted?.Invoke(new AttemptProgress(++done, total, task.Id, task.FamilyId, task.Complexity, repeat, attempt.Outcome, attempt.Score));
                }
            run.Status = ct.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Completed;
        }
        catch
        {
            run.Status = RunStatus.Failed;
            throw;
        }
        finally
        {
            run.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
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
                    await Task.Delay(policy.Delay(attempt.Retries++), cts.Token);
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
