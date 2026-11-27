using System.Text;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Grading;
using ModelBench.Core.Metrics;
using ModelBench.Core.Persistence;
using ModelBench.Core.Sandbox;

namespace ModelBench.Core.Execution;


public sealed record ImportReport(int Graded, IReadOnlyList<string> Errors);

/// <summary>Export prompts for a Manual run to paste into a chat UI, and import the pasted responses for grading.</summary>
public static class ManualExchange
{
    private sealed record PromptLine(Guid RunId, Guid TaskId, int Repeat, string? System, string User, AnswerFormat AnswerFormat);
    private sealed record ResponseLine(Guid TaskId, int Repeat, string Response);

    public static int Export(BenchDb db, Guid runId, string outDir)
    {
        var run = db.Runs.Single(r => r.Id == runId);
        if (run.Mode != ExecutionMode.Manual) throw new InvalidOperationException("run is not a Manual run");
        Directory.CreateDirectory(outDir);
        var tasks = db.Tasks.Where(t => t.SuiteId == run.SuiteId && t.Mode == ExecutionMode.SingleTurn).OrderBy(t => t.Index).ToList();
        var jsonl = new StringBuilder();
        var md = new StringBuilder($"# Manual run {runId}\n\nPaste each prompt into your chat UI and save the reply in responses.jsonl as one JSON line: " +
                                   "{\"taskId\": ..., \"repeat\": ..., \"response\": ...}\n");
        var count = 0;
        foreach (var t in tasks)
        {
            var p = JsonUtil.Deserialize<TaskPrompt>(t.PromptJson);
            for (var r = 0; r < run.Repeats; r++, count++)
            {
                jsonl.AppendLine(JsonUtil.Serialize(new PromptLine(runId, t.Id, r, p.System, p.User, p.AnswerFormat)));
                md.Append($"\n---\n\n## Task {t.Index} ({t.FamilyId}, complexity {t.Complexity}) repeat {r}\n\ntaskId: `{t.Id}`\n\n");
                if (!string.IsNullOrEmpty(p.System)) md.Append($"System:\n\n{p.System}\n\n");
                md.Append($"{p.User}\n");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "prompts.jsonl"), jsonl.ToString());
        File.WriteAllText(Path.Combine(outDir, "prompts.md"), md.ToString());
        return count;
    }

    public static async Task<ImportReport> ImportAsync(BenchDb db, Guid runId, string file, ISandbox sandbox, CancellationToken ct)
    {
        var run = await db.Runs.SingleAsync(r => r.Id == runId, ct);
        if (run.Mode != ExecutionMode.Manual) throw new InvalidOperationException("run is not a Manual run");
        RunService.RequireCurrentGradingVersion(run);
        var tasks = await db.Tasks.Where(t => t.SuiteId == run.SuiteId && t.Mode == ExecutionMode.SingleTurn).ToDictionaryAsync(t => t.Id, ct);
        var existing = (await db.Attempts.Where(a => a.RunId == runId).Select(a => new { a.TaskId, a.RepeatIndex }).ToListAsync(ct))
            .Select(a => (a.TaskId, a.RepeatIndex)).ToHashSet();
        var errors = new List<string>();
        var graded = 0;
        var lineNo = 0;
        foreach (var line in File.ReadLines(file))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            ResponseLine? rl;
            try { rl = JsonUtil.Deserialize<ResponseLine>(line); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { errors.Add($"line {lineNo}: {ex.Message}"); continue; }
            if (!tasks.TryGetValue(rl.TaskId, out var task)) { errors.Add($"line {lineNo}: task {rl.TaskId} is not in this run's suite"); continue; }
            if (rl.Repeat < 0 || rl.Repeat >= run.Repeats) { errors.Add($"line {lineNo}: repeat {rl.Repeat} outside 0..{run.Repeats - 1}"); continue; }
            if (!existing.Add((task.Id, rl.Repeat))) { errors.Add($"line {lineNo}: task {task.Id} repeat {rl.Repeat} already has a response"); continue; }

            var prompt = JsonUtil.Deserialize<TaskPrompt>(task.PromptJson);
            var transcript = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(prompt.System)) transcript.Add(new ChatMessage(Role.System, prompt.System));
            transcript.Add(new ChatMessage(Role.User, prompt.User));
            transcript.Add(new ChatMessage(Role.Assistant, rl.Response));

            var attempt = new Attempt { RunId = runId, TaskId = task.Id, RepeatIndex = rl.Repeat, StartedAt = DateTimeOffset.UtcNow };
            (await GradingPipeline.GradeAsync(prompt, JsonUtil.Deserialize<TaskHidden>(task.HiddenJson), transcript, null, sandbox, ct)).ApplyTo(attempt);
            attempt.TranscriptJson = JsonUtil.Serialize(transcript);
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            attempt.Metrics.AddRange(MetricCalculator.AllUnavailable());
            db.Attempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            graded++;
        }
        run.Status = existing.Count >= tasks.Count * run.Repeats ? RunStatus.Completed : RunStatus.Running;
        if (run.Status == RunStatus.Completed) run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ImportReport(graded, errors);
    }
}
