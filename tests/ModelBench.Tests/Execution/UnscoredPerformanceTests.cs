using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Grading;
using ModelBench.Core.Persistence;
using ModelBench.Core.Providers;
using ModelBench.Core.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.Execution;

/// <summary>Performance is empirical: it never moves Score or Passed (whatever the stored spec says) and never times out an attempt (PR #5 review).</summary>
public class UnscoredPerformanceTests
{
    private static readonly GraderSpec Weighted = new("performance", "p",
        JsonUtil.Element(new { language = "python", entryPoint = "f", args = new[] { 21 }, expected = 42, budgetMs = 100 }), Weight: 5, Required: true);

    private static SandboxResult Right => new(true, null, [new CaseResult(0, JsonDocument.Parse("42").RootElement, null, 0.5)], null, false, 0, "");

    private sealed class DelayedSandbox(int delayMs, SandboxResult result) : ISandbox
    {
        public async Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken ct) { await Task.Delay(delayMs, ct); return result; }
    }

    [Fact] public async Task AWeightedRequiredPerformanceSpecNeverMovesScoreOrPassed()
    {
        // A frozen v1 suite or a static definition may still carry weight 5 / required: v2 forces it unscored.
        var prompt = TaskPrompt.Simple("q");
        var transcript = new[] { new ChatMessage(Role.Assistant, "<answer>x</answer>") };
        foreach (var sandbox in new ISandbox[] { new FakeSandbox(_ => Task.FromResult(Right)), new FakeSandbox(_ => Task.FromResult(FakeSandbox.TimedOut)), new FakeSandbox(_ => Task.FromResult(FakeSandbox.Crashed)) })
        {
            var g = await GradingPipeline.GradeAsync(prompt, new TaskHidden([Weighted]), transcript, null, sandbox, default);
            Assert.Equal((1.0, true), (g.Score, g.Passed));
            Assert.All(g.Assertions.Where(a => a.Grader == "performance"), a => Assert.Equal((0.0, false), (a.Weight, a.Required)));
            Assert.Equal(2, g.Assertions.Count(a => a.Grader == "performance"));
        }
    }

    private static (TempDir, BenchDb, Run, ModelBench.Core.Config.ResolvedModel) Setup(int timeoutSeconds)
    {
        var dir = new TempDir();
        var db = dir.OpenDb();
        var suite = TestKit.MakeSuite(db, 1);
        var task = db.Tasks.Single(t => t.SuiteId == suite.Id);
        task.HiddenJson = JsonUtil.Serialize(new TaskHidden([Weighted])); // answer-format + performance only
        db.SaveChanges();
        var model = TestKit.Model("http://127.0.0.1:9/v1");
        return (dir, db, RunService.CreateRun(db, suite.Id, model, ExecutionMode.SingleTurn, 1, timeoutSeconds, new RetryPolicy(), null), model);
    }

    [Fact] public async Task APerformanceRunLongerThanTheRemainingAttemptBudgetGradesTheAttemptInsteadOfTimingItOut()
    {
        var (dir, db, run, model) = Setup(timeoutSeconds: 1);
        using (dir) using (db)
        {
            var provider = new ScriptedProvider([new ChatMessage(Role.Assistant, "<answer>def f(x): return x * 2</answer>")]);
            await new RunExecutor(provider, model, new DelayedSandbox(1500, Right)).ExecuteAsync(db, run.Id, default); // 1.5 s of sandbox work, 1 s attempt budget
            var a = db.Attempts.AsNoTracking().Include(x => x.Assertions).Single();
            Assert.Equal((AttemptOutcome.Passed, 1.0), (a.Outcome, a.Score));
            Assert.False(a.Assertions.Single(x => x.Name == "within-budget").Passed); // reported, not scored
        }
    }

    [Fact] public async Task RegradeIgnoresAPerformanceRunThatSucceededThenTimesOutAndBack()
    {
        var (dir, db, run, model) = Setup(timeoutSeconds: 30);
        using (dir) using (db)
        {
            var provider = new ScriptedProvider([new ChatMessage(Role.Assistant, "<answer>def f(x): return x * 2</answer>")]);
            await new RunExecutor(provider, model, new FakeSandbox(_ => Task.FromResult(Right))).ExecuteAsync(db, run.Id, default);
            Assert.True(db.Assertions.Single(x => x.Name == "correct").Passed);

            foreach (var sandbox in new[] { FakeSandbox.TimedOut, Right, FakeSandbox.TimedOut })
            {
                var report = await Regrader.RunAsync(db, run.Id, new FakeSandbox(_ => Task.FromResult(sandbox)), default);
                Assert.Equal(1, report.Attempts);
                Assert.Empty(report.Differences);
            }
        }
    }
}
