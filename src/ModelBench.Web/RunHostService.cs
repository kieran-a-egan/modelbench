using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Config;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;

namespace ModelBench.Web;

/// <summary>Executes enqueued runs one at a time in the background. The only place the dashboard touches RunExecutor.</summary>
public sealed class RunHostService(Func<BenchDb> openDb, ILogger<RunHostService> log) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, (ResolvedModel Model, CancellationTokenSource Cts, bool RetryInfra)> _pending = new();

    public void Enqueue(Guid runId, ResolvedModel model, bool retryInfra = false)
    {
        // Idempotent: a second enqueue of a run already queued or executing (e.g. Resume racing the initial Enqueue) is a no-op.
        if (_pending.TryAdd(runId, (model, new CancellationTokenSource(), retryInfra))) _queue.Writer.TryWrite(runId);
    }

    /// <summary>True while this process has the run queued or executing.</summary>
    public bool IsTracked(Guid runId) => _pending.ContainsKey(runId);

    /// <summary>Validates and queues a resume (see RunService.PrepareResume). Throws InvalidOperationException with the reason when refused.</summary>
    public void Resume(Guid runId, ModelCatalog catalog, bool retryInfra)
    {
        if (_pending.ContainsKey(runId)) throw new InvalidOperationException("this run is already queued or executing in this process");
        ResolvedModel model;
        using (var db = openDb()) model = RunService.PrepareResume(db, runId, catalog);
        Enqueue(runId, model, retryInfra);
    }

    /// <summary>Cancels a queued or running run (in-process token, plus the DB flag so the state is visible to other processes); the executor marks it Cancelled.</summary>
    public void Cancel(Guid runId)
    {
        if (_pending.TryGetValue(runId, out var p)) p.Cts.Cancel();
        using var db = openDb();
        try { RunService.RequestCancel(db, runId); }
        catch (InvalidOperationException) { } // already finished
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_pending.TryGetValue(id, out var p)) continue;
            try
            {
                using var db = openDb();
                var executor = new RunExecutor(ProviderFactory.Create(p.Model), p.Model, new DockerSandbox());
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(p.Cts.Token, stoppingToken);
                await executor.ExecuteAsync(db, id, linked.Token, p.RetryInfra);
            }
            catch (RunLeaseException ex)
            {
                log.LogWarning("run {RunId} not executed: {Reason}", id, ex.Message); // another executor owns it; leave its status alone
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "run {RunId} failed", id);
                using var db = openDb();
                await db.Runs.Where(r => r.Id == id && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Failed), CancellationToken.None);
            }
            finally
            {
                if (_pending.TryRemove(id, out var done)) done.Cts.Dispose();
            }
        }
    }
}
