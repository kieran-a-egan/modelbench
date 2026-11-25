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
    private readonly ConcurrentDictionary<Guid, (ResolvedModel Model, CancellationTokenSource Cts)> _pending = new();

    public void Enqueue(Guid runId, ResolvedModel model)
    {
        _pending[runId] = (model, new CancellationTokenSource());
        _queue.Writer.TryWrite(runId);
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
                await executor.ExecuteAsync(db, id, linked.Token);
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
