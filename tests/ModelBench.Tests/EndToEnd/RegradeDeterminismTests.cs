using Microsoft.EntityFrameworkCore;
using ModelBench.Cli;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.EndToEnd;

/// <summary>Routes each call to the script for its task, found by the task's user prompt.</summary>
internal sealed class PerTaskProvider(Dictionary<string, ScriptedProvider> byPrompt) : IModelProvider
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
        => byPrompt[request.Messages.First(m => m.Role == Role.User).Content!].CompleteAsync(request, ct);
}

/// <summary>The real sandbox, but single-input runs (the performance case) take an extra delay on the host clock.</summary>
internal sealed class SlowHost(ISandbox inner, int delayMs) : ISandbox
{
    public async Task<SandboxResult> ExecuteAsync(SandboxRequest r, CancellationToken ct)
    {
        var result = await inner.ExecuteAsync(r, ct);
        if (r.Action == SandboxAction.Run && r.Inputs.Count == 1) await Task.Delay(delayMs, ct);
        return result;
    }
}

public class RegradeDeterminismTests
{
    [Fact] public async Task HostTimingChangesNeitherTheScoreNorTheRegradeVerdict()
    {
        // MB-002: within-budget is empirical; the same answer graded on a slow host must score and pass identically.
        var sandbox = await SandboxEnv.Require();
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        var suite = SuiteGenerator.Generate(new SuiteSpec("perf", 11, [new FamilySpec("code-generation", 3, 1)]));
        SuiteGenerator.Persist(db, suite);
        var task = suite.Tasks.Single();
        var gen = FamilyRegistry.Get(task.FamilyId).Generate(new Rng(task.Seed), 3, 0);
        var perf = gen.Hidden.Graders.Single(g => g.Type == "performance");
        Assert.Equal((0.0, false), (perf.Weight, perf.Required));

        var model = TestKit.Model("http://127.0.0.1:9/v1");
        var run = RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, 120, new RetryPolicy(), null);
        await new RunExecutor(new ScriptedProvider(gen.ReferenceTurns), model, sandbox).ExecuteAsync(db, run.Id, CancellationToken.None);
        var stored = db.Attempts.AsNoTracking().Include(a => a.Assertions).Single();
        Assert.Equal((AttemptOutcome.Passed, 1.0), (stored.Outcome, stored.Score));
        Assert.True(stored.Assertions.Single(a => a.Name == "within-budget").Passed);

        // The unscored "correct" assertion is not compared either: a kill at the budget under load would flip it.
        var correct = db.Assertions.Single(a => a.AttemptId == stored.Id && a.Name == "correct");
        correct.Passed = false;
        db.SaveChanges();

        var slow = new SlowHost(sandbox, perf.Config.GetProperty("budgetMs").GetInt32() + 200);
        var report = await Regrader.RunAsync(db, run.Id, slow, CancellationToken.None);
        Assert.Empty(report.Differences);
        // The slow host really did miss the budget, so the comparison was skipped rather than coincidentally equal.
        var regraded = await ModelBench.Core.Grading.GradingPipeline.GradeAsync(JsonUtil.Deserialize<TaskPrompt>(task.PromptJson), JsonUtil.Deserialize<TaskHidden>(task.HiddenJson),
            JsonUtil.Deserialize<List<ChatMessage>>(stored.TranscriptJson!), null, slow, CancellationToken.None);
        Assert.False(regraded.Assertions.Single(a => a.Name == "within-budget").Passed);
        Assert.Equal((true, 1.0), (regraded.Passed, regraded.Score));
    }

    private static async Task<(int, string)> Cli(params string[] args)
    {
        using var sw = new StringWriter();
        var code = await CliApp.RunAsync(args, sw, sw);
        return (code, sw.ToString());
    }

    [Fact] public async Task RegradeCheckReproducesEveryStoredAttemptAndDetectsTampering()
    {
        var sandbox = await SandboxEnv.Require(); // code families grade in the real sandbox
        using var dir = new TempDir();
        using var db = dir.OpenDb();
        // Families without a reference answer (file-defined without one) cannot be scripted.
        var fams = FamilyRegistry.All
            .Where(f => f.Generate(new Rng(1), 2, 0).ReferenceTurns.Count > 0).ToList();
        var suite = SuiteGenerator.Generate(new SuiteSpec("regrade", 7, [.. fams.Select(f => new FamilySpec(f.Id, 2, 1))]));
        SuiteGenerator.Persist(db, suite);

        // Even tasks replay the reference turns, odd tasks the first known-bad turns.
        var scripts = new Dictionary<string, ScriptedProvider>();
        foreach (var t in suite.Tasks)
        {
            var gen = FamilyRegistry.Get(t.FamilyId).Generate(new Rng(t.Seed), 2, 0);
            scripts[gen.Prompt.User] = new ScriptedProvider(t.Index % 2 == 0 || gen.KnownBadTurns.Count == 0 ? gen.ReferenceTurns : gen.KnownBadTurns[0]);
        }

        var model = TestKit.Model("http://127.0.0.1:9/v1");
        var executor = new RunExecutor(new PerTaskProvider(scripts), model, sandbox);
        foreach (var mode in suite.Tasks.Select(t => t.Mode).Distinct())
            await executor.ExecuteAsync(db, RunService.CreateRun(db, suite.Id, model, mode, 1, 120, new RetryPolicy(), mode.ToString()).Id, CancellationToken.None);

        var attempts = db.Attempts.AsNoTracking().ToList();
        Assert.Equal(suite.Tasks.Count, attempts.Count);
        Assert.Contains(attempts, a => a.Outcome == AttemptOutcome.Passed);
        Assert.Contains(attempts, a => a.Outcome == AttemptOutcome.Failed);

        foreach (var run in db.Runs.AsNoTracking().ToList())
        {
            var (code, output) = await Cli("--db", dir.File("test.db"), "run", "regrade", run.Id.ToString(), "--check");
            Assert.True(code == 0, output);
            Assert.Contains(" 0 with differences", output);
        }

        // A tampered stored assertion is detected; without --check the command only reports and never writes.
        var victim = db.Assertions.AsNoTracking().First(a => a.Grader != "answer-format");
        var runId = db.Attempts.Where(a => a.Id == victim.AttemptId).Select(a => a.RunId).Single();
        db.Assertions.Where(a => a.Id == victim.Id).ExecuteUpdate(s => s.SetProperty(a => a.Actual, "tampered"));
        var (c1, o1) = await Cli("--db", dir.File("test.db"), "run", "regrade", runId.ToString(), "--check");
        Assert.Equal(1, c1);
        Assert.Contains("DIFF", o1);
        Assert.Contains("tampered", o1);
        var (c2, o2) = await Cli("--db", dir.File("test.db"), "run", "regrade", runId.ToString());
        Assert.Equal(0, c2);
        Assert.Contains("DIFF", o2);
        Assert.Equal("tampered", db.Assertions.AsNoTracking().Single(a => a.Id == victim.Id).Actual);
    }
}
