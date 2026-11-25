using Microsoft.EntityFrameworkCore;
using ModelBench.Cli;
using ModelBench.Core.Domain;
using ModelBench.Core.Execution;
using ModelBench.Core.Families;
using ModelBench.Core.Generation;
using ModelBench.Core.Providers;
using ModelBench.Tests.Sandbox;
using ModelBench.Tests.Support;

namespace ModelBench.Tests.EndToEnd;

/// <summary>Routes each call to the script for its task, found by the task's user prompt.</summary>
internal sealed class PerTaskProvider(Dictionary<string, ScriptedProvider> byPrompt) : IModelProvider
{
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
        => byPrompt[request.Messages.First(m => m.Role == Role.User).Content!].CompleteAsync(request, ct);
}

public class RegradeDeterminismTests
{
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

        var model = TestKit.Model("http://unused");
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
