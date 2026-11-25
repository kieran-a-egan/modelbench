using System.Text;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Families;
using ModelBench.Core.Grading;
using ModelBench.Core.Persistence;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Execution;

public sealed record RegradeDifference(Guid AttemptId, int TaskIndex, string FamilyId, int Repeat, string Problem);

public sealed record RegradeReport(int Attempts, IReadOnlyList<RegradeDifference> Differences);

/// <summary>
/// Re-runs the grading pipeline on every stored graded attempt (AC-01) and compares with the stored result. Read-only.
/// The final environment state of tool attempts is reproduced by replaying the transcript's tool calls into a fresh environment.
/// </summary>
public static class Regrader
{
    public static async Task<RegradeReport> RunAsync(BenchDb db, Guid runId, ISandbox sandbox, CancellationToken ct)
    {
        if (!await db.Runs.AnyAsync(r => r.Id == runId, ct)) throw new InvalidOperationException($"run {runId} not found");
        var attempts = await db.Attempts.AsNoTracking().Include(a => a.Task).Include(a => a.Assertions)
            .Where(a => a.RunId == runId && (a.Outcome == AttemptOutcome.Passed || a.Outcome == AttemptOutcome.Failed) && a.TranscriptJson != null)
            .ToListAsync(ct);
        var diffs = new List<RegradeDifference>();
        foreach (var a in attempts.OrderBy(a => a.Task!.Index).ThenBy(a => a.RepeatIndex))
        {
            var task = a.Task!;
            var prompt = JsonUtil.Deserialize<TaskPrompt>(task.PromptJson);
            var hidden = JsonUtil.Deserialize<TaskHidden>(task.HiddenJson);
            var transcript = JsonUtil.Deserialize<List<ChatMessage>>(a.TranscriptJson!);
            var state = Replay(FamilyRegistry.Get(task.FamilyId), hidden, transcript);
            var graded = await GradingPipeline.GradeAsync(prompt, hidden, transcript, state, sandbox, ct);

            var problems = new List<string>();
            if (graded.Score != a.Score) problems.Add($"score {a.Score} -> {graded.Score}");
            if ((graded.Passed ? AttemptOutcome.Passed : AttemptOutcome.Failed) != a.Outcome) problems.Add($"outcome {a.Outcome} -> {(graded.Passed ? "Passed" : "Failed")}");
            var stored = a.Assertions.OrderBy(x => x.Id).ToList();
            if (stored.Count != graded.Assertions.Count) problems.Add($"assertion count {stored.Count} -> {graded.Assertions.Count}");
            for (var i = 0; i < Math.Min(stored.Count, graded.Assertions.Count); i++)
            {
                var (s, n) = (stored[i], graded.Assertions[i]);
                if (s.Grader != n.Grader || s.Name != n.Name || s.Expected != n.Expected || s.Actual != n.Actual || s.Score != n.Score || s.Passed != n.Passed)
                    problems.Add($"assertion {i} '{s.Name}': stored (expected [{s.Expected}] actual [{s.Actual}] score {s.Score} passed {s.Passed}) vs regraded (expected [{n.Expected}] actual [{n.Actual}] score {n.Score} passed {n.Passed})");
            }
            if (problems.Count > 0) diffs.Add(new RegradeDifference(a.Id, task.Index, task.FamilyId, a.RepeatIndex, string.Join("; ", problems)));
        }
        return new RegradeReport(attempts.Count, diffs);
    }

    /// <summary>Final environment state after executing the transcript's tool calls; null for families without an environment.</summary>
    private static System.Text.Json.JsonElement? Replay(IBenchmarkFamily family, TaskHidden hidden, IReadOnlyList<ChatMessage> transcript)
    {
        var env = family.CreateEnvironment(hidden);
        if (env is null) return null;
        for (var i = 0; i < transcript.Count - 1; i++) // tool calls on the last message were never executed (step limit)
            foreach (var call in transcript[i].ToolCalls ?? [])
                if (Encoding.UTF8.GetByteCount(call.ArgumentsJson) <= EpisodeRunner.MaxToolArgsBytes) env.Execute(call);
        return env.State;
    }
}
